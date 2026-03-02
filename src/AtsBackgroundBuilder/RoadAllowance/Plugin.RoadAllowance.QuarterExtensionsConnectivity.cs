using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static void ExtendQuarterLinesFromUsecWestSouthToNextUsec(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
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
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    // Accept multi-vertex open polylines only when effectively collinear.
                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var usecBoundarySegments = new List<(Point2d A, Point2d B)>();
                var sourceVerticalUsecSegments = new List<(Point2d A, Point2d B)>();
                var secTargetSegments = new List<(Point2d A, Point2d B)>();
                var generatedVerticalUsecTargets = new List<(Point2d A, Point2d B)>();
                var qsecLineIds = new List<ObjectId>();
                var lsdLineIds = new List<ObjectId>();
                var qsecVerticalSegments = new List<(Point2d A, Point2d B)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        lsdLineIds.Add(id);
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsVerticalLike(a, b))
                        {
                            qsecVerticalSegments.Add((a, b));
                        }

                        qsecLineIds.Add(id);
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        secTargetSegments.Add((a, b));
                        var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        var isSecLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                        if ((isUsecLayer || isSecLayer) && IsVerticalLike(a, b))
                        {
                            generatedVerticalUsecTargets.Add((a, b));
                        }

                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        usecBoundarySegments.Add((a, b));
                        if (IsVerticalLike(a, b))
                        {
                            sourceVerticalUsecSegments.Add((a, b));
                        }
                    }
                }

                if (qsecLineIds.Count == 0 || secTargetSegments.Count == 0 || usecBoundarySegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double touchTol = 0.20;
                const double minExtend = 0.05;
                const double maxExtend = 40.0;
                const double endpointMoveTol = 0.05;
                var adjusted = 0;
                var horizontalQsecMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();

                foreach (var id in qsecLineIds)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var lineDir = p1 - p0;
                    if (lineDir.Length <= 1e-4)
                    {
                        continue;
                    }

                    var isVerticalQsec = IsVerticalLike(p0, p1);
                    // Apply explicit rule:
                    // - Vertical 1/4 line: extend only S.1/4 endpoint (south end)
                    // - Horizontal 1/4 line: extend only W.1/4 endpoint (west end)
                    var selectedOriginal = p0;
                    var other = p1;
                    if (isVerticalQsec)
                    {
                        if (p1.Y < p0.Y)
                        {
                            selectedOriginal = p1;
                            other = p0;
                        }
                    }
                    else
                    {
                        if (p1.X < p0.X)
                        {
                            selectedOriginal = p1;
                            other = p0;
                        }
                    }

                    var touchesRelevantUsec = false;
                    for (var si = 0; si < usecBoundarySegments.Count; si++)
                    {
                        var boundary = usecBoundarySegments[si];
                        if (isVerticalQsec && !IsHorizontalLike(boundary.A, boundary.B))
                        {
                            continue;
                        }

                        if (!isVerticalQsec && !IsVerticalLike(boundary.A, boundary.B))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(selectedOriginal, boundary.A, boundary.B) <= touchTol)
                        {
                            touchesRelevantUsec = true;
                            break;
                        }
                    }

                    if (!touchesRelevantUsec)
                    {
                        continue;
                    }

                    var outward = selectedOriginal - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        continue;
                    }

                    var outwardDir = outward / outwardLen;
                    double? bestTargetDistance = null;
                    for (var ti = 0; ti < secTargetSegments.Count; ti++)
                    {
                        var target = secTargetSegments[ti];
                        if (isVerticalQsec && !IsHorizontalLike(target.A, target.B))
                        {
                            continue;
                        }

                        if (!isVerticalQsec && !IsVerticalLike(target.A, target.B))
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLineWithSegment(selectedOriginal, outwardDir, target.A, target.B, out var t))
                        {
                            continue;
                        }

                        if (t <= minExtend || t > maxExtend)
                        {
                            continue;
                        }

                        if (!bestTargetDistance.HasValue || t < bestTargetDistance.Value)
                        {
                            bestTargetDistance = t;
                        }
                    }

                    if (!bestTargetDistance.HasValue)
                    {
                        continue;
                    }

                    var selectedNew = selectedOriginal + (outwardDir * bestTargetDistance.Value);
                    if (selectedNew.GetDistanceTo(selectedOriginal) <= endpointMoveTol)
                    {
                        continue;
                    }

                    if (writable is Line ln)
                    {
                        if (selectedOriginal.GetDistanceTo(p0) <= selectedOriginal.GetDistanceTo(p1))
                        {
                            ln.StartPoint = new Point3d(selectedNew.X, selectedNew.Y, ln.StartPoint.Z);
                        }
                        else
                        {
                            ln.EndPoint = new Point3d(selectedNew.X, selectedNew.Y, ln.EndPoint.Z);
                        }
                    }
                    else if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        if (selectedOriginal.GetDistanceTo(p0) <= selectedOriginal.GetDistanceTo(p1))
                        {
                            pl.SetPointAt(0, selectedNew);
                        }
                        else
                        {
                            pl.SetPointAt(1, selectedNew);
                        }
                    }
                    else
                    {
                        continue;
                    }

                    adjusted++;
                    if (!isVerticalQsec)
                    {
                        var centerAnchor = other;
                        var towardCenterVec = other - selectedOriginal;
                        var towardCenterLen = towardCenterVec.Length;
                        if (towardCenterLen > 1e-6 && qsecVerticalSegments.Count > 0)
                        {
                            var towardCenterDir = towardCenterVec / towardCenterLen;
                            double? bestCenterT = null;
                            const double centerEndpointTol = 1.0;
                            for (var vi = 0; vi < qsecVerticalSegments.Count; vi++)
                            {
                                var vseg = qsecVerticalSegments[vi];
                                if (!TryIntersectInfiniteLineWithSegment(selectedOriginal, towardCenterDir, vseg.A, vseg.B, out var tCenter))
                                {
                                    continue;
                                }

                                if (tCenter <= centerEndpointTol || tCenter >= (towardCenterLen - centerEndpointTol))
                                {
                                    continue;
                                }

                                if (!bestCenterT.HasValue || tCenter < bestCenterT.Value)
                                {
                                    bestCenterT = tCenter;
                                }
                            }

                            if (bestCenterT.HasValue)
                            {
                                centerAnchor = selectedOriginal + (towardCenterDir * bestCenterT.Value);
                            }
                        }

                        horizontalQsecMidpointAdjustments.Add((
                            selectedOriginal,
                            centerAnchor,
                            Midpoint(selectedOriginal, centerAnchor),
                            Midpoint(selectedNew, centerAnchor)));
                    }
                }

                var lsdAdjusted = 0;
                if (horizontalQsecMidpointAdjustments.Count > 0 && lsdLineIds.Count > 0)
                {
                    const double lsdOnOldQsecTol = 0.35;
                    const double lsdOldMidTol = 12.0;
                    const double lsdMaxMove = 25.0;

                    bool TryMappedMidpoint(Point2d endpoint, out Point2d mappedMid, out double bestSegDistance, out double bestMidDistance)
                    {
                        mappedMid = endpoint;
                        bestSegDistance = double.MaxValue;
                        bestMidDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;

                        for (var i = 0; i < horizontalQsecMidpointAdjustments.Count; i++)
                        {
                            var adj = horizontalQsecMidpointAdjustments[i];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOnOldQsecTol)
                            {
                                continue;
                            }

                            var midDistance = endpoint.GetDistanceTo(adj.OldMid);
                            if (midDistance > lsdOldMidTol)
                            {
                                continue;
                            }

                            var moveDistance = endpoint.GetDistanceTo(adj.NewMid);
                            if (moveDistance <= endpointMoveTol || moveDistance > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && midDistance < (bestMidDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(midDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && moveDistance < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestMidDistance = midDistance;
                            bestMoveDistance = moveDistance;
                            mappedMid = adj.NewMid;
                        }

                        return bestSegDistance < double.MaxValue;
                    }

                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryMappedMidpoint(p0, out var mid0, out var seg0, out var md0);
                        var has1 = TryMappedMidpoint(p1, out var mid1, out var seg1, out var md1);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var moveStart = has0;
                        var targetMid = mid0;
                        if (!has0 || (has1 && (seg1 < seg0 || (Math.Abs(seg1 - seg0) <= 1e-6 && md1 < md0))))
                        {
                            moveStart = false;
                            targetMid = mid1;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices >= 2)
                        {
                            var index = moveStart ? 0 : lsdPoly.NumberOfVertices - 1;
                            lsdPoly.SetPointAt(index, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                var westHalfLsdAdjusted = 0;
                if (lsdLineIds.Count > 0 && sourceVerticalUsecSegments.Count > 0 && generatedVerticalUsecTargets.Count > 0)
                {
                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1) || !IsHorizontalLike(p0, p1))
                        {
                            continue;
                        }

                        var west = p0;
                        var east = p1;
                        if (east.X < west.X)
                        {
                            var tmp = west;
                            west = east;
                            east = tmp;
                        }

                        // Strict source gate: only LSD endpoints anchored on original L-USEC (30.16) boundaries.
                        var touchesOriginalUsec = false;
                        for (var si = 0; si < sourceVerticalUsecSegments.Count; si++)
                        {
                            var src = sourceVerticalUsecSegments[si];
                            if (DistancePointToSegment(west, src.A, src.B) <= touchTol)
                            {
                                touchesOriginalUsec = true;
                                break;
                            }
                        }

                        if (!touchesOriginalUsec)
                        {
                            continue;
                        }

                        var outward = west - east;
                        var outwardLen = outward.Length;
                        if (outwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var outwardDir = outward / outwardLen;
                        double? bestTargetDistance = null;
                        const double westHalfTargetDistance = CorrectionLinePairGapMeters;
                        const double westHalfTargetTol = 2.8;
                        var bestTargetScore = double.MaxValue;
                        for (var ti = 0; ti < generatedVerticalUsecTargets.Count; ti++)
                        {
                            var target = generatedVerticalUsecTargets[ti];
                            if (!TryIntersectInfiniteLineWithSegment(west, outwardDir, target.A, target.B, out var t))
                            {
                                continue;
                            }

                            if (t <= minExtend || t > maxExtend)
                            {
                                continue;
                            }

                            var score = Math.Abs(t - westHalfTargetDistance);
                            if (score > westHalfTargetTol)
                            {
                                continue;
                            }

                            if (!bestTargetDistance.HasValue ||
                                score < (bestTargetScore - 1e-9) ||
                                (Math.Abs(score - bestTargetScore) <= 1e-9 && t < bestTargetDistance.Value))
                            {
                                bestTargetDistance = t;
                                bestTargetScore = score;
                            }
                        }

                        if (!bestTargetDistance.HasValue)
                        {
                            continue;
                        }

                        var westNew = west + (outwardDir * bestTargetDistance.Value);
                        if (westNew.GetDistanceTo(west) <= endpointMoveTol)
                        {
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            var d0 = west.GetDistanceTo(p0);
                            var d1 = west.GetDistanceTo(p1);
                            if (d0 <= d1)
                            {
                                lsdLine.StartPoint = new Point3d(westNew.X, westNew.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(westNew.X, westNew.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            var d0 = west.GetDistanceTo(p0);
                            var d1 = west.GetDistanceTo(p1);
                            lsdPoly.SetPointAt(d0 <= d1 ? 0 : 1, westNew);
                        }
                        else
                        {
                            continue;
                        }

                        westHalfLsdAdjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: extended {adjusted} L-QSEC W.1/4/S.1/4 endpoint(s) to next L-USEC line.");
                }
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of W.1/4 L-QSEC extension line(s).");
                }
                if (westHalfLsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: extended {westHalfLsdAdjusted} W.1/2 E-W L-SECTION-LSD endpoint(s) from original L-USEC to generated 20.11 boundary.");
                }
            }
        }

        private static void ExtendSouthBoundarySwQuarterWestToNextUsec(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
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
                    if (pl.Closed || pl.NumberOfVertices != 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool Near(Point2d p, Point2d q, double tol)
            {
                return p.GetDistanceTo(q) <= tol;
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var sourceSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool IsUsec, bool IsSec)>();
                var verticalUsecBoundaries = new List<(Point2d A, Point2d B)>();
                var generatedUsecVerticalTargets = new List<(Point2d A, Point2d B, bool IsUsecLayer)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        var isUsecGeneratedLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        var isSecGeneratedLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                        if ((isUsecGeneratedLayer || isSecGeneratedLayer) && IsVerticalLike(a, b))
                        {
                            generatedUsecVerticalTargets.Add((a, b, isUsecGeneratedLayer));
                        }

                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add((id, a, b));
                        }

                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSecLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    sourceSegments.Add((id, a, b, isUsecLayer, isSecLayer));
                    if (isUsecLayer && IsVerticalLike(a, b))
                    {
                        verticalUsecBoundaries.Add((a, b));
                    }
                }

                if (sourceSegments.Count == 0 || verticalUsecBoundaries.Count == 0 || generatedUsecVerticalTargets.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double touchTol = 0.20;
                const double cornerTol = 0.10;
                const double minExtend = 0.05;
                const double maxExtend = 40.0;
                const double twentyTwelveBandMin = 7.2;
                const double twentyTwelveBandMax = 13.4;
                const double endpointMoveTol = 0.05;
                var adjusted = 0;
                var secHorizontalSources = 0;
                var secHorizontalWithTwentyBandTarget = 0;
                var secHorizontalRejectedNoTwentyBandTarget = 0;
                var lsdMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var movedSourceSegments = new List<(ObjectId Id, Point2d OldA, Point2d OldB, Point2d NewA, Point2d NewB)>();

                foreach (var src in sourceSegments)
                {
                    // Never shift original L-USEC south-boundary segments to generated 20.11 targets.
                    if (src.IsUsec)
                    {
                        continue;
                    }

                    if (!IsHorizontalLike(src.A, src.B))
                    {
                        continue;
                    }
                    secHorizontalSources++;

                    var west = src.A;
                    var east = src.B;
                    if (east.X < west.X)
                    {
                        var tmp = west;
                        west = east;
                        east = tmp;
                    }

                    // Must be west-half style segment: east endpoint has horizontal continuation to the east.
                    var hasEastContinuation = false;
                    foreach (var other in sourceSegments)
                    {
                        if (other.Id == src.Id || !IsHorizontalLike(other.A, other.B))
                        {
                            continue;
                        }

                        if (Near(east, other.A, cornerTol) && other.B.X > (east.X + cornerTol))
                        {
                            hasEastContinuation = true;
                            break;
                        }

                        if (Near(east, other.B, cornerTol) && other.A.X > (east.X + cornerTol))
                        {
                            hasEastContinuation = true;
                            break;
                        }
                    }

                    if (!hasEastContinuation)
                    {
                        continue;
                    }

                    // Must touch west vertical L-USEC and represent SW corner behavior
                    // (connected vertical line goes north from this corner).
                    var touchesWestUsec = false;
                    var hasNorthwardVertical = false;
                    foreach (var boundary in verticalUsecBoundaries)
                    {
                        var d = DistancePointToSegment(west, boundary.A, boundary.B);
                        if (d > touchTol)
                        {
                            continue;
                        }

                        touchesWestUsec = true;
                        var da = west.GetDistanceTo(boundary.A);
                        var db = west.GetDistanceTo(boundary.B);
                        var far = da >= db ? boundary.A : boundary.B;
                        if (far.Y > (west.Y + cornerTol))
                        {
                            hasNorthwardVertical = true;
                        }
                    }

                    if (!touchesWestUsec || !hasNorthwardVertical)
                    {
                        continue;
                    }

                    var outward = west - east;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        continue;
                    }

                    var outwardDir = outward / outwardLen;
                    double? bestT = null;
                    var targetCandidates = new List<(double T, bool IsUsecLayer)>();
                    for (var ti = 0; ti < generatedUsecVerticalTargets.Count; ti++)
                    {
                        var target = generatedUsecVerticalTargets[ti];
                        if (!TryIntersectInfiniteLineWithSegment(west, outwardDir, target.A, target.B, out var t))
                        {
                            continue;
                        }

                        if (t <= minExtend || t > maxExtend)
                        {
                            continue;
                        }

                        targetCandidates.Add((t, target.IsUsecLayer));
                    }

                    for (var ti = 0; ti < verticalUsecBoundaries.Count; ti++)
                    {
                        var target = verticalUsecBoundaries[ti];
                        if (!TryIntersectInfiniteLineWithSegment(west, outwardDir, target.A, target.B, out var t))
                        {
                            continue;
                        }

                        if (t <= minExtend || t > maxExtend)
                        {
                            continue;
                        }

                        targetCandidates.Add((t, true));
                    }

                    if (targetCandidates.Count > 0)
                    {
                        var twentyUsecCandidates = targetCandidates
                            .Where(c => c.IsUsecLayer &&
                                        c.T >= twentyTwelveBandMin &&
                                        c.T <= twentyTwelveBandMax)
                            .OrderBy(c => c.T)
                            .ToList();
                        var twentyAnyCandidates = targetCandidates
                            .Where(c => c.T >= twentyTwelveBandMin &&
                                        c.T <= twentyTwelveBandMax)
                            .OrderBy(c => c.T)
                            .ToList();

                        if (src.IsSec)
                        {
                            if (twentyUsecCandidates.Count > 0)
                            {
                                bestT = twentyUsecCandidates[0].T;
                                secHorizontalWithTwentyBandTarget++;
                            }
                            else if (twentyAnyCandidates.Count > 0)
                            {
                                bestT = twentyAnyCandidates[0].T;
                                secHorizontalWithTwentyBandTarget++;
                            }
                            else
                            {
                                secHorizontalRejectedNoTwentyBandTarget++;
                            }
                        }
                    }

                    var finalWest = west;
                    if (bestT.HasValue)
                    {
                        finalWest = west + (outwardDir * bestT.Value);
                    }
                    else
                    {
                        var westAtGeneratedTarget = false;
                        for (var gi = 0; gi < generatedUsecVerticalTargets.Count; gi++)
                        {
                            var target = generatedUsecVerticalTargets[gi];
                            if (DistancePointToSegment(west, target.A, target.B) <= touchTol)
                            {
                                westAtGeneratedTarget = true;
                                break;
                            }
                        }

                        if (!westAtGeneratedTarget)
                        {
                            continue;
                        }
                    }

                    var eastAnchor = east;

                    if (finalWest.GetDistanceTo(west) <= endpointMoveTol)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(src.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (writable is Line ln)
                    {
                        if (west.GetDistanceTo(src.A) <= west.GetDistanceTo(src.B))
                        {
                            ln.StartPoint = new Point3d(finalWest.X, finalWest.Y, ln.StartPoint.Z);
                        }
                        else
                        {
                            ln.EndPoint = new Point3d(finalWest.X, finalWest.Y, ln.EndPoint.Z);
                        }
                    }
                    else if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        if (west.GetDistanceTo(src.A) <= west.GetDistanceTo(src.B))
                        {
                            pl.SetPointAt(0, finalWest);
                        }
                        else
                        {
                            pl.SetPointAt(1, finalWest);
                        }
                    }
                    else
                    {
                        continue;
                    }

                    adjusted++;
                    lsdMidpointAdjustments.Add((west, eastAnchor, Midpoint(west, eastAnchor), Midpoint(finalWest, eastAnchor)));
                    movedSourceSegments.Add((src.Id, west, eastAnchor, finalWest, eastAnchor));
                }

                var blindSiblingErased = 0;
                if (movedSourceSegments.Count > 0)
                {
                    const double siblingEndpointTol = 0.35;
                    var movedIds = new HashSet<ObjectId>(movedSourceSegments.Select(m => m.Id));
                    foreach (var source in sourceSegments)
                    {
                        if (movedIds.Contains(source.Id))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity sibling) || sibling.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(sibling, out var aSibling, out var bSibling) || !IsHorizontalLike(aSibling, bSibling))
                        {
                            continue;
                        }

                        var erase = false;
                        for (var mi = 0; mi < movedSourceSegments.Count; mi++)
                        {
                            var moved = movedSourceSegments[mi];
                            if (!AreSegmentsDuplicateOrCollinearOverlap(aSibling, bSibling, moved.OldA, moved.OldB))
                            {
                                continue;
                            }

                            if (!AreSegmentEndpointsNear(aSibling, bSibling, moved.OldA, moved.OldB, siblingEndpointTol))
                            {
                                continue;
                            }

                            if (AreSegmentEndpointsNear(aSibling, bSibling, moved.NewA, moved.NewB, siblingEndpointTol))
                            {
                                continue;
                            }

                            erase = true;
                            break;
                        }

                        if (!erase)
                        {
                            continue;
                        }

                        sibling.Erase();
                        blindSiblingErased++;
                    }
                }

                var lsdAdjusted = 0;
                if (lsdMidpointAdjustments.Count > 0 && lsdSegments.Count > 0)
                {
                    const double lsdOldSegmentTol = 0.35;
                    const double lsdOldMidpointTol = 8.5;
                    const double lsdMaxMove = 80.0;

                    bool TryBestMidpoint(Point2d endpoint, out Point2d midpoint, out double bestDistance, out double moveDistance)
                    {
                        midpoint = endpoint;
                        bestDistance = double.MaxValue;
                        moveDistance = double.MaxValue;
                        var bestSegDistance = double.MaxValue;

                        for (var ei = 0; ei < lsdMidpointAdjustments.Count; ei++)
                        {
                            var adj = lsdMidpointAdjustments[ei];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(adj.OldMid);
                            if (d > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var isBetterSegment = segDistance < (bestSegDistance - 1e-6);
                            var isTiedSegment = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var isBetterMidDistance = isTiedSegment && d < (bestDistance - 1e-6);
                            var isTiedMidDistance = isTiedSegment && Math.Abs(d - bestDistance) <= 1e-6;
                            var isBetterMoveOnTie = isTiedMidDistance && move < moveDistance;
                            if (!isBetterSegment && !isBetterMidDistance && !isBetterMoveOnTie)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestDistance = d;
                            moveDistance = move;
                            midpoint = adj.NewMid;
                        }

                        return bestDistance < double.MaxValue;
                    }

                    foreach (var lsd in lsdSegments)
                    {
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryBestMidpoint(p0, out var mid0, out var d0, out _);
                        var has1 = TryBestMidpoint(p1, out var mid1, out var d1, out _);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var moveStart = has0;
                        var targetMid = mid0;
                        if (!has0 || (has1 && d1 < d0))
                        {
                            moveStart = false;
                            targetMid = mid1;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            lsdPoly.SetPointAt(moveStart ? 0 : 1, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: extended {adjusted} SW south-boundary west endpoint(s) to next L-USEC line.");
                }
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of SW south-boundary extension line(s) [segment-anchored].");
                }
                if (blindSiblingErased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {blindSiblingErased} blind-line sibling segment(s) after SW extension.");
                }
                logger?.WriteLine(
                    $"Cleanup: SW south-boundary 20.11 target stats sources={secHorizontalSources}, with20.11Target={secHorizontalWithTwentyBandTarget}, rejectedNo20.11Target={secHorizontalRejectedNoTwentyBandTarget}, adjusted={adjusted}.");
            }
        }
        private static void ExtendNwQuarterWestUsecNorthToNextHorizontalUsec(
            Database database,
            IEnumerable<QuarterLabelInfo> labelQuarterInfos,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || labelQuarterInfos == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var requestedQuarterIds = labelQuarterInfos
                .Where(info =>
                    info != null &&
                    info.Quarter == QuarterSelection.NorthWest &&
                    !info.QuarterId.IsNull)
                .Select(info => info.QuarterId)
                .Distinct()
                .ToList();
            if (requestedQuarterIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
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
                    if (pl.Closed || pl.NumberOfVertices != 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
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

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var horizontalSources = new List<(ObjectId Id, Point2d A, Point2d B, bool Generated)>();
                var generatedVerticalUsec = new List<(ObjectId Id, Point2d North, Point2d South)>();
                var verticalRoadBoundaries = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var horizontalRoadBoundaries = new List<(ObjectId Id, Point2d A, Point2d B, bool IsSec, bool IsUsec, bool Generated)>();
                var verticalRoadCandidates = new List<(ObjectId Id, Point2d A, Point2d B, bool IsSec, bool IsUsec, bool Generated)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add((id, a, b));
                        }

                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    var generated = generatedSet.Contains(id);
                    if (isUsec && generated && IsHorizontalLike(a, b))
                    {
                        horizontalSources.Add((id, a, b, generated));
                    }
                    if (IsHorizontalLike(a, b))
                    {
                        horizontalRoadBoundaries.Add((id, a, b, isSec, isUsec, generated));
                    }

                    if ((isUsec || isSec) && IsVerticalLike(a, b))
                    {
                        verticalRoadBoundaries.Add((id, a, b));
                        verticalRoadCandidates.Add((id, a, b, isSec, isUsec, generated));
                    }

                    if (isUsec && generated && IsVerticalLike(a, b))
                    {
                        var north = a;
                        var south = b;
                        if (south.Y > north.Y)
                        {
                            var tmp = north;
                            north = south;
                            south = tmp;
                        }

                        generatedVerticalUsec.Add((id, north, south));
                    }
                }

                var canRunLegacyPass = horizontalSources.Count > 0 && generatedVerticalUsec.Count > 0;

                const double endpointMoveTol = 0.05;
                const double searchRadius = 18.0;
                const double maxExtend = 40.0;
                const double boundaryHitTol = 0.35;
                const double boundaryEndpointTol = 0.20;
                const double classTwentyGap = 20.11;
                const double classThirtyGap = RoadAllowanceUsecWidthMeters;
                const double classTenGap = CorrectionLinePairGapMeters;
                const double classTwentyTol = 1.10;
                const double classThirtyTol = 1.60;
                const double classTenTol = 2.40;
                const double classMinOverlap = 4.0;
                const int roadClassUnknown = 0;
                const int roadClassTwenty = 20;
                const int roadClassThirty = 30;

                var roadClassSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Axis, double MajorMin, double MajorMax)>();
                var roadClassById = new Dictionary<ObjectId, int>();
                var blindRoadIds = new HashSet<ObjectId>();
                {
                    var classSeen = new HashSet<ObjectId>();
                    for (var i = 0; i < horizontalRoadBoundaries.Count; i++)
                    {
                        var seg = horizontalRoadBoundaries[i];
                        if (!classSeen.Add(seg.Id))
                        {
                            continue;
                        }

                        var axis = 0.5 * (seg.A.Y + seg.B.Y);
                        roadClassSegments.Add((seg.Id, seg.A, seg.B, true, false, axis, Math.Min(seg.A.X, seg.B.X), Math.Max(seg.A.X, seg.B.X)));
                    }

                    for (var i = 0; i < verticalRoadCandidates.Count; i++)
                    {
                        var seg = verticalRoadCandidates[i];
                        if (!classSeen.Add(seg.Id))
                        {
                            continue;
                        }

                        var axis = 0.5 * (seg.A.X + seg.B.X);
                        roadClassSegments.Add((seg.Id, seg.A, seg.B, false, true, axis, Math.Min(seg.A.Y, seg.B.Y), Math.Max(seg.A.Y, seg.B.Y)));
                    }

                    var hasTwenty = new bool[roadClassSegments.Count];
                    var hasThirty = new bool[roadClassSegments.Count];
                    var hasTen = new bool[roadClassSegments.Count];
                    for (var i = 0; i < roadClassSegments.Count; i++)
                    {
                        var a = roadClassSegments[i];
                        for (var j = i + 1; j < roadClassSegments.Count; j++)
                        {
                            var b = roadClassSegments[j];
                            if ((a.Horizontal != b.Horizontal) || (a.Vertical != b.Vertical))
                            {
                                continue;
                            }

                            var overlap = Math.Min(a.MajorMax, b.MajorMax) - Math.Max(a.MajorMin, b.MajorMin);
                            if (overlap < classMinOverlap)
                            {
                                continue;
                            }

                            var gap = Math.Abs(a.Axis - b.Axis);
                            if (Math.Abs(gap - classTwentyGap) <= classTwentyTol)
                            {
                                hasTwenty[i] = true;
                                hasTwenty[j] = true;
                            }

                            if (Math.Abs(gap - classThirtyGap) <= classThirtyTol)
                            {
                                hasThirty[i] = true;
                                hasThirty[j] = true;
                            }

                            if (Math.Abs(gap - classTenGap) <= classTenTol)
                            {
                                hasTen[i] = true;
                                hasTen[j] = true;
                            }
                        }
                    }

                    bool HasAnyTouch(ObjectId selfId, Point2d endpoint)
                    {
                        for (var ri = 0; ri < roadClassSegments.Count; ri++)
                        {
                            var road = roadClassSegments[ri];
                            if (road.Id == selfId)
                            {
                                continue;
                            }

                            if (DistancePointToSegment(endpoint, road.A, road.B) <= boundaryHitTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    for (var i = 0; i < roadClassSegments.Count; i++)
                    {
                        var seg = roadClassSegments[i];
                        var cls = roadClassUnknown;
                        if (hasThirty[i] && hasTen[i])
                        {
                            cls = roadClassThirty;
                        }
                        else if (hasTwenty[i] || hasTen[i] || hasThirty[i])
                        {
                            cls = roadClassTwenty;
                        }

                        roadClassById[seg.Id] = cls;

                        var endATouch = HasAnyTouch(seg.Id, seg.A);
                        var endBTouch = HasAnyTouch(seg.Id, seg.B);
                        if (!endATouch || !endBTouch)
                        {
                            blindRoadIds.Add(seg.Id);
                        }
                    }
                }

                bool IsAllowedClassConnection(
                    ObjectId sourceId,
                    ObjectId targetId)
                {
                    roadClassById.TryGetValue(sourceId, out var sourceClass);
                    roadClassById.TryGetValue(targetId, out var targetClass);

                    if (sourceClass == roadClassTwenty)
                    {
                        return targetClass == roadClassTwenty || blindRoadIds.Contains(targetId);
                    }

                    if (sourceClass == roadClassThirty)
                    {
                        return targetClass == roadClassThirty;
                    }

                    return true;
                }

                bool TryGetNearestThirtyVerticalAxis(Point2d referencePoint, double overlapMinY, double overlapMaxY, out double axisX)
                {
                    axisX = 0.0;
                    var bestScore = double.MaxValue;
                    var found = false;
                    for (var i = 0; i < roadClassSegments.Count; i++)
                    {
                        var road = roadClassSegments[i];
                        if (!road.Vertical)
                        {
                            continue;
                        }

                        if (!roadClassById.TryGetValue(road.Id, out var cls) || cls != roadClassThirty)
                        {
                            continue;
                        }

                        var overlap = Math.Min(overlapMaxY, road.MajorMax) - Math.Max(overlapMinY, road.MajorMin);
                        if (overlap < classMinOverlap)
                        {
                            continue;
                        }

                        var score = Math.Abs(referencePoint.X - road.Axis);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            axisX = road.Axis;
                            found = true;
                        }
                    }

                    return found;
                }

                bool IsTwentyHorizontalToVerticalSameSideConnection(
                    ObjectId sourceId,
                    Point2d sourceA,
                    Point2d sourceB,
                    Point2d sourceReference,
                    ObjectId targetId,
                    Point2d targetA,
                    Point2d targetB,
                    Point2d targetReference)
                {
                    if (!roadClassById.TryGetValue(sourceId, out var sourceClass) || sourceClass != roadClassTwenty)
                    {
                        return true;
                    }

                    if (!roadClassById.TryGetValue(targetId, out var targetClass) || targetClass != roadClassTwenty)
                    {
                        return true;
                    }

                    if (!IsHorizontalLike(sourceA, sourceB) || !IsVerticalLike(targetA, targetB))
                    {
                        return true;
                    }

                    var targetMinY = Math.Min(targetA.Y, targetB.Y);
                    var targetMaxY = Math.Max(targetA.Y, targetB.Y);
                    if (!TryGetNearestThirtyVerticalAxis(sourceReference, targetMinY, targetMaxY, out var axisX))
                    {
                        return true;
                    }

                    var sourceDx = sourceReference.X - axisX;
                    var targetDx = targetReference.X - axisX;
                    if (Math.Abs(sourceDx) <= boundaryEndpointTol || Math.Abs(targetDx) <= boundaryEndpointTol)
                    {
                        return true;
                    }

                    return Math.Sign(sourceDx) == Math.Sign(targetDx);
                }

                bool HasInterveningVerticalRoad(Point2d from, Point2d to, ObjectId anchorId)
                {
                    for (var i = 0; i < verticalRoadBoundaries.Count; i++)
                    {
                        var candidate = verticalRoadBoundaries[i];
                        if (candidate.Id == anchorId)
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLines(from, to, candidate.A, candidate.B, out var hit))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(hit, from, to) > boundaryHitTol)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(hit, candidate.A, candidate.B) > boundaryHitTol)
                        {
                            continue;
                        }

                        if (hit.GetDistanceTo(from) <= boundaryEndpointTol ||
                            hit.GetDistanceTo(to) <= boundaryEndpointTol)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool TryAddConnectorSegment(Point2d from, Point2d to, string layerName)
                {
                    if (to.GetDistanceTo(from) <= endpointMoveTol)
                    {
                        return false;
                    }

                    try
                    {
                        var btWrite = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                        var msWrite = (BlockTableRecord)tr.GetObject(btWrite[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        var connector = new Line(
                            new Point3d(from.X, from.Y, 0.0),
                            new Point3d(to.X, to.Y, 0.0));
                        if (!string.IsNullOrWhiteSpace(layerName))
                        {
                            connector.Layer = layerName;
                        }

                        msWrite.AppendEntity(connector);
                        tr.AddNewlyCreatedDBObject(connector, true);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                var adjusted = 0;
                var usedHorizontals = new HashSet<ObjectId>();
                var pairTried = 0;
                var pairChosen = 0;
                var pairAmbiguousSkipped = 0;
                var blockedByInterveningVerticalRoad = 0;
                var classRuleRejected = 0;
                var twentySideRuleRejected = 0;
                var lsdMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var movedHorizontalSegments = new List<(ObjectId Id, Point2d OldA, Point2d OldB, Point2d NewA, Point2d NewB)>();

                if (canRunLegacyPass)
                {
                    for (var pass = 0; pass < 2; pass++)
                    {
                        var allowGenerated = pass == 1;
                        var adjustedThisPass = 0;

                        foreach (var anchor in generatedVerticalUsec)
                        {
                            var bestId = ObjectId.Null;
                            var bestOld = default(Point2d);
                            var bestOther = default(Point2d);
                            var bestNew = default(Point2d);
                            var bestScore = double.MaxValue;
                            var secondBestScore = double.MaxValue;

                            for (var i = 0; i < horizontalSources.Count; i++)
                            {
                                var src = horizontalSources[i];
                                if (!allowGenerated && src.Generated)
                                {
                                    continue;
                                }

                                if (usedHorizontals.Contains(src.Id))
                                {
                                    continue;
                                }

                                var endpoint0 = src.A;
                                var endpoint1 = src.B;
                                var anchorPoint = endpoint0.GetDistanceTo(anchor.South) <= endpoint1.GetDistanceTo(anchor.South)
                                    ? anchor.South
                                    : anchor.North;
                                var movePoint = endpoint0;
                                var otherPoint = endpoint1;
                                if (endpoint1.GetDistanceTo(anchorPoint) < endpoint0.GetDistanceTo(anchorPoint))
                                {
                                    movePoint = endpoint1;
                                    otherPoint = endpoint0;
                                }

                                var midpointTarget = Midpoint(anchor.North, anchor.South);

                                var dx = src.B.X - src.A.X;
                                if (Math.Abs(dx) <= 1e-8)
                                {
                                    continue;
                                }

                                var t = (anchorPoint.X - src.A.X) / dx;
                                var yTarget = src.A.Y + (t * (src.B.Y - src.A.Y));
                                var candidateTarget = new Point2d(anchorPoint.X, yTarget);

                                var moveDist = movePoint.GetDistanceTo(candidateTarget);
                                if (moveDist <= endpointMoveTol || moveDist > maxExtend)
                                {
                                    continue;
                                }

                                var anchorDist = movePoint.GetDistanceTo(anchorPoint);
                                if (anchorDist > searchRadius)
                                {
                                    continue;
                                }

                                const double midpointSnapTol = 0.10;
                                var finalTarget = candidateTarget;
                                if (candidateTarget.GetDistanceTo(midpointTarget) <= midpointSnapTol)
                                {
                                    finalTarget = midpointTarget;
                                }

                                if (!IsAllowedClassConnection(src.Id, anchor.Id))
                                {
                                    classRuleRejected++;
                                    continue;
                                }

                                if (!IsTwentyHorizontalToVerticalSameSideConnection(
                                        src.Id,
                                        src.A,
                                        src.B,
                                        movePoint,
                                        anchor.Id,
                                        anchor.North,
                                        anchor.South,
                                        anchorPoint))
                                {
                                    twentySideRuleRejected++;
                                    continue;
                                }

                                if (HasInterveningVerticalRoad(movePoint, finalTarget, anchor.Id))
                                {
                                    blockedByInterveningVerticalRoad++;
                                    continue;
                                }

                                var score = moveDist + (2.0 * Math.Abs(movePoint.Y - anchorPoint.Y));
                                pairTried++;
                                if (score < bestScore)
                                {
                                    secondBestScore = bestScore;
                                    bestScore = score;
                                    bestId = src.Id;
                                    bestOld = movePoint;
                                    bestOther = otherPoint;
                                    bestNew = finalTarget;
                                }
                                else if (score < secondBestScore)
                                {
                                    secondBestScore = score;
                                }
                            }

                            if (bestId.IsNull)
                            {
                                continue;
                            }

                            // Skip ambiguous picks where two opposite-side candidates score nearly the same.
                            if (secondBestScore < double.MaxValue && (secondBestScore - bestScore) < 1.0)
                            {
                                pairAmbiguousSkipped++;
                                continue;
                            }

                            if (!(tr.GetObject(bestId, OpenMode.ForRead, false) is Entity readable) || readable.IsErased)
                            {
                                continue;
                            }

                            // Non-destructive: add connector only, do not move existing endpoints.
                            if (TryAddConnectorSegment(bestOld, bestNew, readable.Layer))
                            {
                                adjusted++;
                                adjustedThisPass++;
                                pairChosen++;
                                usedHorizontals.Add(bestId);
                            }
                        }

                        if (adjustedThisPass > 0)
                        {
                            break;
                        }
                    }
                }

                var blindSiblingErased = 0;
                if (movedHorizontalSegments.Count > 0)
                {
                    const double siblingEndpointTol = 0.35;
                    var movedIds = new HashSet<ObjectId>(movedHorizontalSegments.Select(m => m.Id));
                    for (var i = 0; i < horizontalSources.Count; i++)
                    {
                        var source = horizontalSources[i];
                        if (movedIds.Contains(source.Id))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity sibling) || sibling.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(sibling, out var aSibling, out var bSibling) || !IsHorizontalLike(aSibling, bSibling))
                        {
                            continue;
                        }

                        var erase = false;
                        for (var mi = 0; mi < movedHorizontalSegments.Count; mi++)
                        {
                            var moved = movedHorizontalSegments[mi];
                            if (!AreSegmentsDuplicateOrCollinearOverlap(aSibling, bSibling, moved.OldA, moved.OldB))
                            {
                                continue;
                            }

                            if (!AreSegmentEndpointsNear(aSibling, bSibling, moved.OldA, moved.OldB, siblingEndpointTol))
                            {
                                continue;
                            }

                            if (AreSegmentEndpointsNear(aSibling, bSibling, moved.NewA, moved.NewB, siblingEndpointTol))
                            {
                                continue;
                            }

                            erase = true;
                            break;
                        }

                        if (!erase)
                        {
                            continue;
                        }

                        sibling.Erase();
                        blindSiblingErased++;
                    }
                }

                var lsdAdjusted = 0;
                if (lsdMidpointAdjustments.Count > 0 && lsdSegments.Count > 0)
                {
                    const double lsdOldSegmentTol = 0.35;
                    const double lsdOldMidpointTol = 12.0;
                    const double lsdMaxMove = 40.0;

                    bool TryBestMidpoint(Point2d endpoint, out Point2d midpoint, out double bestDistance)
                    {
                        midpoint = endpoint;
                        bestDistance = double.MaxValue;
                        var bestSegDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;

                        for (var i = 0; i < lsdMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdMidpointAdjustments[i];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(adj.OldMid);
                            if (d > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && d < (bestDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(d - bestDistance) <= 1e-6;
                            var betterMove = tiedMid && move < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestDistance = d;
                            bestMoveDistance = move;
                            midpoint = adj.NewMid;
                        }

                        return bestDistance < double.MaxValue;
                    }

                    for (var i = 0; i < lsdSegments.Count; i++)
                    {
                        var lsd = lsdSegments[i];
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryBestMidpoint(p0, out var mid0, out var d0);
                        var has1 = TryBestMidpoint(p1, out var mid1, out var d1);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var moveStart = has0;
                        var targetMid = mid0;
                        if (!has0 || (has1 && d1 < d0))
                        {
                            moveStart = false;
                            targetMid = mid1;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            lsdPoly.SetPointAt(moveStart ? 0 : 1, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                var nwSecondWestSecAdjusted = 0;
                var nwSecondWestSecCandidates = 0;
                var nwSecondWestSecWithNorthTarget = 0;
                var nwSecondWestSecUsecFallback = 0;
                var nwSecondWestSecRejectedSouthHalf = 0;
                var nwSecondWestSecMissingSecondCluster = 0;
                var nwSecondWestSecApparentCandidates = 0;
                var nwSecondWestSecApparentAdjusted = 0;
                const double westBandWidth = 75.0;
                const double northBandHeight = 80.0;
                const double minNorthJoin = 0.05;
                const double maxNorthJoin = 45.0;
                const double endpointTouchTol = 1.0;
                const double apparentSpanGapMax = 15.0;
                const double apparentJoinDistanceMax = 2.0;
                const double northSpanTol = 0.35;
                const double windowBandTol = 0.35;
                const double secondWestClusterTol = 2.0;
                const double minSelectedVerticalLength = 40.0;
                const double quarterWindowBufferMeters = 100.0;
                const double westOwnershipWidthMax = 35.0;

                bool SegmentIntersectsWindow(Point2d a, Point2d b, Extents3d window)
                {
                    if (a.X >= window.MinPoint.X && a.X <= window.MaxPoint.X &&
                        a.Y >= window.MinPoint.Y && a.Y <= window.MaxPoint.Y)
                    {
                        return true;
                    }

                    if (b.X >= window.MinPoint.X && b.X <= window.MaxPoint.X &&
                        b.Y >= window.MinPoint.Y && b.Y <= window.MaxPoint.Y)
                    {
                        return true;
                    }

                    return TryClipSegmentToWindow(a, b, window, out _, out _);
                }

                for (var wi = 0; wi < clipWindows.Count; wi++)
                {
                    var window = clipWindows[wi];
                    var coreMinX = window.MinPoint.X + quarterWindowBufferMeters;
                    var coreMaxX = window.MaxPoint.X - quarterWindowBufferMeters;
                    var coreMinY = window.MinPoint.Y + quarterWindowBufferMeters;
                    var coreMaxY = window.MaxPoint.Y - quarterWindowBufferMeters;
                    if (coreMaxX <= coreMinX || coreMaxY <= coreMinY)
                    {
                        coreMinX = window.MinPoint.X;
                        coreMaxX = window.MaxPoint.X;
                        coreMinY = window.MinPoint.Y;
                        coreMaxY = window.MaxPoint.Y;
                    }

                    var westBandMinX = coreMinX - westOwnershipWidthMax;
                    var westBandMaxX = coreMinX + westBandWidth;
                    var northBandMinY = coreMaxY - northBandHeight;
                    var northHalfMinY = (0.5 * (coreMinY + coreMaxY)) - 0.20;

                    var westCandidates = new List<(ObjectId Id, Point2d A, Point2d B, bool IsSec, bool Generated, double MidX, double NorthY, double Length)>();

                    for (var vi = 0; vi < verticalRoadCandidates.Count; vi++)
                    {
                        var candidate = verticalRoadCandidates[vi];
                        if (!SegmentIntersectsWindow(candidate.A, candidate.B, window))
                        {
                            continue;
                        }

                        var midX = 0.5 * (candidate.A.X + candidate.B.X);
                        if (midX < (westBandMinX - windowBandTol) || midX > (westBandMaxX + windowBandTol))
                        {
                            continue;
                        }

                        var candidateNorthY = Math.Max(candidate.A.Y, candidate.B.Y);
                        if (candidateNorthY < northHalfMinY)
                        {
                            nwSecondWestSecRejectedSouthHalf++;
                            continue;
                        }
                        westCandidates.Add((
                            candidate.Id,
                            candidate.A,
                            candidate.B,
                            candidate.IsSec,
                            candidate.Generated,
                            midX,
                            candidateNorthY,
                            candidate.A.GetDistanceTo(candidate.B)));
                    }

                    if (westCandidates.Count == 0)
                    {
                        continue;
                    }

                    var sourceCandidates = westCandidates
                        .Where(c => c.IsSec)
                        .ToList();
                    var selectedFromUsecFallback = false;
                    if (sourceCandidates.Count == 0)
                    {
                        sourceCandidates = westCandidates;
                        selectedFromUsecFallback = true;
                    }

                    var sortedWestCandidates = sourceCandidates
                        .OrderBy(c => c.MidX)
                        .ToList();
                    var westClusters = new List<List<(ObjectId Id, Point2d A, Point2d B, bool IsSec, bool Generated, double MidX, double NorthY, double Length)>>();
                    for (var i = 0; i < sortedWestCandidates.Count; i++)
                    {
                        var candidate = sortedWestCandidates[i];
                        if (westClusters.Count == 0)
                        {
                            westClusters.Add(new List<(ObjectId Id, Point2d A, Point2d B, bool IsSec, bool Generated, double MidX, double NorthY, double Length)> { candidate });
                            continue;
                        }

                        var lastCluster = westClusters[westClusters.Count - 1];
                        var lastClusterX = lastCluster.Average(c => c.MidX);
                        if (Math.Abs(candidate.MidX - lastClusterX) <= secondWestClusterTol)
                        {
                            lastCluster.Add(candidate);
                        }
                        else
                        {
                            westClusters.Add(new List<(ObjectId Id, Point2d A, Point2d B, bool IsSec, bool Generated, double MidX, double NorthY, double Length)> { candidate });
                        }
                    }

                    if (westClusters.Count < 2)
                    {
                        // Safety: this rule is defined as second-most-west. If we cannot form at least
                        // two distinct west-side clusters, do not fall back to westernmost.
                        nwSecondWestSecMissingSecondCluster++;
                        continue;
                    }

                    var selectedCluster = westClusters[1];
                    var nonStubCluster = selectedCluster
                        .Where(c => c.Length >= minSelectedVerticalLength)
                        .ToList();
                    var rankedCluster = nonStubCluster.Count > 0
                        ? nonStubCluster
                        : selectedCluster;
                    var selectedWestSec = rankedCluster
                        .OrderByDescending(c => c.NorthY)
                        .ThenByDescending(c => c.Length)
                        .ThenByDescending(c => c.MidX)
                        .ThenBy(c => c.Generated)
                        .First();
                    var selectedWestHandle = selectedWestSec.Id.Handle.ToString();
                    if (selectedFromUsecFallback)
                    {
                        nwSecondWestSecUsecFallback++;
                    }
                    nwSecondWestSecCandidates++;

                    var westSec = (selectedWestSec.Id, selectedWestSec.A, selectedWestSec.B, selectedWestSec.Generated);
                    if (!(tr.GetObject(westSec.Id, OpenMode.ForRead, false) is Entity readableWestSec) || readableWestSec.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(readableWestSec, out var v0, out var v1) || !IsVerticalLike(v0, v1))
                    {
                        continue;
                    }

                    var northEndpoint = v0.Y >= v1.Y ? v0 : v1;
                    var verticalX = northEndpoint.X;

                    var hasJoin = false;
                    var bestJoinPoint = default(Point2d);
                    var bestJoinDistance = double.MaxValue;
                    var hasApparentJoin = false;
                    var bestApparentJoinPoint = default(Point2d);
                    var bestApparentEndpoint = default(Point2d);
                    var bestApparentHorizontalId = ObjectId.Null;
                    var bestApparentScore = double.MaxValue;

                    for (var hi = 0; hi < horizontalRoadBoundaries.Count; hi++)
                    {
                        var horizontal = horizontalRoadBoundaries[hi];
                        if (!SegmentIntersectsWindow(horizontal.A, horizontal.B, window))
                        {
                            continue;
                        }

                        var yMid = 0.5 * (horizontal.A.Y + horizontal.B.Y);
                        if (yMid < northBandMinY)
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLines(v0, v1, horizontal.A, horizontal.B, out var joinPoint))
                        {
                            continue;
                        }

                        if (!IsAllowedClassConnection(westSec.Id, horizontal.Id))
                        {
                            classRuleRejected++;
                            continue;
                        }

                        if (joinPoint.Y < (northEndpoint.Y - 0.25))
                        {
                            continue;
                        }

                        var joinDistance = northEndpoint.GetDistanceTo(joinPoint);
                        var endpointTouch = joinDistance <= endpointTouchTol;
                        var regularJoin = joinDistance > minNorthJoin && joinDistance <= maxNorthJoin;
                        if (!endpointTouch && !regularJoin)
                        {
                            continue;
                        }

                        var minX = Math.Min(horizontal.A.X, horizontal.B.X) - northSpanTol;
                        var maxX = Math.Max(horizontal.A.X, horizontal.B.X) + northSpanTol;
                        var spansVertical = verticalX >= minX && verticalX <= maxX;
                        if (spansVertical)
                        {
                            if (DistancePointToSegment(joinPoint, horizontal.A, horizontal.B) > boundaryHitTol)
                            {
                                continue;
                            }

                            if (!hasJoin || joinDistance < bestJoinDistance)
                            {
                                hasJoin = true;
                                bestJoinDistance = joinDistance;
                                bestJoinPoint = joinPoint;
                            }

                            continue;
                        }

                        var xGap = verticalX < minX
                            ? (minX - verticalX)
                            : (verticalX - maxX);
                        if (xGap > apparentSpanGapMax || joinDistance > apparentJoinDistanceMax)
                        {
                            continue;
                        }

                        var endpointA = horizontal.A;
                        var endpointB = horizontal.B;
                        var moveEndpoint = endpointA.GetDistanceTo(joinPoint) <= endpointB.GetDistanceTo(joinPoint)
                            ? endpointA
                            : endpointB;
                        var endpointMove = moveEndpoint.GetDistanceTo(joinPoint);
                        if (endpointMove > (apparentSpanGapMax + 2.0))
                        {
                            continue;
                        }

                        var apparentScore = endpointMove + (2.0 * joinDistance);
                        if (!hasApparentJoin || apparentScore < bestApparentScore)
                        {
                            hasApparentJoin = true;
                            bestApparentScore = apparentScore;
                            bestApparentHorizontalId = horizontal.Id;
                            bestApparentEndpoint = moveEndpoint;
                            bestApparentJoinPoint = joinPoint;
                        }
                    }

                    if (!hasJoin && !hasApparentJoin)
                    {
                        continue;
                    }
                    nwSecondWestSecWithNorthTarget++;

                    var preferApparent =
                        hasApparentJoin &&
                        (!hasJoin || (bestApparentScore + 0.25) < bestJoinDistance);

                    if (hasJoin && !preferApparent)
                    {
                        if (!HasInterveningVerticalRoad(northEndpoint, bestJoinPoint, westSec.Id))
                        {
                            if (TryAddConnectorSegment(northEndpoint, bestJoinPoint, readableWestSec.Layer))
                            {
                                nwSecondWestSecAdjusted++;
                                adjusted++;
                                if (EnableRoadAllowanceDiagnostics)
                                {
                                    logger?.WriteLine(
                                        $"RA-DIAG NW-2WEST DIRECT W{wi + 1}: src={selectedWestHandle} join=({bestJoinPoint.X:0.###},{bestJoinPoint.Y:0.###}) dist={bestJoinDistance:0.###}");
                                }
                                continue;
                            }
                        }
                        else
                        {
                            blockedByInterveningVerticalRoad++;
                        }
                    }

                    if (!hasApparentJoin || bestApparentHorizontalId.IsNull)
                    {
                        continue;
                    }
                    nwSecondWestSecApparentCandidates++;

                    if (HasInterveningVerticalRoad(bestApparentEndpoint, bestApparentJoinPoint, westSec.Id))
                    {
                        blockedByInterveningVerticalRoad++;
                        continue;
                    }

                    if (!(tr.GetObject(bestApparentHorizontalId, OpenMode.ForRead, false) is Entity readableHorizontal) || readableHorizontal.IsErased)
                    {
                        continue;
                    }

                    if (TryAddConnectorSegment(bestApparentEndpoint, bestApparentJoinPoint, readableHorizontal.Layer))
                    {
                        nwSecondWestSecAdjusted++;
                        nwSecondWestSecApparentAdjusted++;
                        adjusted++;
                        if (EnableRoadAllowanceDiagnostics)
                        {
                            logger?.WriteLine(
                                $"RA-DIAG NW-2WEST APPARENT W{wi + 1}: src={selectedWestHandle} tgt={bestApparentHorizontalId.Handle} join=({bestApparentJoinPoint.X:0.###},{bestApparentJoinPoint.Y:0.###})");
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: connected {adjusted} NW RA corner gap endpoint(s) (H={adjusted}, V=0).");
                if (nwSecondWestSecAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: connected {nwSecondWestSecAdjusted} NW second-most-west-side 20.11 line(s) to north quarter boundary.");
                }
                logger?.WriteLine(
                    $"Cleanup: NW second-most-west-side 20.11 diagnostics candidates={nwSecondWestSecCandidates}, withNorthTarget={nwSecondWestSecWithNorthTarget}, adjusted={nwSecondWestSecAdjusted}, usecFallback={nwSecondWestSecUsecFallback}, rejectedSouthHalf={nwSecondWestSecRejectedSouthHalf}, missingSecondCluster={nwSecondWestSecMissingSecondCluster}, apparentCandidates={nwSecondWestSecApparentCandidates}, apparentAdjusted={nwSecondWestSecApparentAdjusted}, classRuleRejected={classRuleRejected}, twentySideRuleRejected={twentySideRuleRejected}.");
                if (blockedByInterveningVerticalRoad > 0)
                {
                    logger?.WriteLine($"Cleanup: NW guard blocked {blockedByInterveningVerticalRoad} candidate endpoint move(s) due to intervening vertical road boundaries.");
                }
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of NW west-end extension line(s).");
                }
                if (blindSiblingErased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {blindSiblingErased} blind-line sibling segment(s) after NW extension.");
                }
                if (adjusted == 0)
                {
                    logger?.WriteLine($"Cleanup: NW simple candidates (H={horizontalSources.Count}, GV={generatedVerticalUsec.Count}, tries={pairTried}, chosen={pairChosen}).");
                }
            }
        }

        private static void ApplyCanonicalRoadAllowanceEndpointRules(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId>? generatedRoadAllowanceIds,
            Logger? logger,
            bool allowBlindFallback = true)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            var generatedSet = generatedRoadAllowanceIds != null && generatedRoadAllowanceIds.Count > 0
                ? new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull))
                : new HashSet<ObjectId>();

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
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
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

                bool TryGetUnitDirection(Point2d a, Point2d b, out Vector2d unit)
                {
                    unit = b - a;
                    var len = unit.Length;
                    if (len <= 1e-9)
                    {
                        return false;
                    }

                    unit = unit / len;
                    return true;
                }

                int SignWithTol(double value, double tol)
                {
                if (value > tol)
                {
                    return 1;
                }

                if (value < -tol)
                {
                    return -1;
                }

                return 0;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var roadSegments = new List<(
                    ObjectId Id,
                    Point2d A,
                    Point2d B,
                    bool Horizontal,
                    bool Vertical,
                    bool IsSecLayer,
                    bool IsUsecLayer,
                    bool Generated,
                    double Axis,
                    double MajorMin,
                    double MajorMax,
                    string Layer)>();

                void AddRoadSegment(
                    ObjectId id,
                    Point2d a,
                    Point2d b,
                    bool isSecLayer,
                    bool isUsecLayer,
                    bool generated,
                    string layer)
                {
                    if (a.GetDistanceTo(b) <= 1e-4 || !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        return;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        return;
                    }

                    var axis = horizontal
                        ? 0.5 * (a.Y + b.Y)
                        : 0.5 * (a.X + b.X);
                    var majorMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var majorMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    roadSegments.Add((id, a, b, horizontal, vertical, isSecLayer, isUsecLayer, generated, axis, majorMin, majorMax, layer));
                }

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layerName = ent.Layer ?? string.Empty;
                    var isUsec = IsUsecLayer(layerName);
                    var isSec = string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    var isGenerated = generatedSet.Contains(id);
                    var layer = layerName;
                    if (ent is Line ln)
                    {
                        AddRoadSegment(
                            id,
                            new Point2d(ln.StartPoint.X, ln.StartPoint.Y),
                            new Point2d(ln.EndPoint.X, ln.EndPoint.Y),
                            isSec,
                            isUsec,
                            isGenerated,
                            layer);
                        continue;
                    }

                    if (!(ent is Polyline pl) || pl.Closed || pl.NumberOfVertices < 2)
                    {
                        continue;
                    }

                    for (var vi = 0; vi < pl.NumberOfVertices - 1; vi++)
                    {
                        AddRoadSegment(id, pl.GetPoint2dAt(vi), pl.GetPoint2dAt(vi + 1), isSec, isUsec, isGenerated, layer);
                    }
                }

                if (roadSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double classTwentyGap = 20.11;
                const double classThirtyGap = RoadAllowanceUsecWidthMeters;
                const double classTenGap = CorrectionLinePairGapMeters;
                const double classTwentyTol = 1.10;
                const double classThirtyTol = 1.60;
                const double classTenTol = 2.40;
                const double classMinOverlap = 4.0;
                const int roadClassUnknown = 0;
                const int roadClassTwenty = 20;
                const int roadClassThirty = 30;

                var hasTwenty = new bool[roadSegments.Count];
                var hasThirty = new bool[roadSegments.Count];
                var hasTen = new bool[roadSegments.Count];
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    var a = roadSegments[i];
                    for (var j = i + 1; j < roadSegments.Count; j++)
                    {
                        var b = roadSegments[j];
                        if ((a.Horizontal != b.Horizontal) || (a.Vertical != b.Vertical))
                        {
                            continue;
                        }

                        var overlap = Math.Min(a.MajorMax, b.MajorMax) - Math.Max(a.MajorMin, b.MajorMin);
                        if (overlap < classMinOverlap)
                        {
                            continue;
                        }

                        var gap = Math.Abs(a.Axis - b.Axis);
                        if (Math.Abs(gap - classTwentyGap) <= classTwentyTol)
                        {
                            hasTwenty[i] = true;
                            hasTwenty[j] = true;
                        }

                        if (Math.Abs(gap - classThirtyGap) <= classThirtyTol)
                        {
                            hasThirty[i] = true;
                            hasThirty[j] = true;
                        }

                        if (Math.Abs(gap - classTenGap) <= classTenTol)
                        {
                            hasTen[i] = true;
                            hasTen[j] = true;
                        }
                    }
                }

                var roadClass = new int[roadSegments.Count];
                var blindSegments = new bool[roadSegments.Count];
                var generatedThirtyHardAsTwenty = new bool[roadSegments.Count];
                const double boundaryHitTol = 0.35;
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    var cls = roadClassUnknown;
                    if (hasThirty[i] && hasTen[i])
                    {
                        cls = roadClassThirty;
                    }
                    else if (hasTwenty[i] || hasTen[i] || hasThirty[i])
                    {
                        cls = roadClassTwenty;
                    }

                    roadClass[i] = cls;

                    bool EndpointTouchesAnyOther(Point2d endpoint)
                    {
                        for (var j = 0; j < roadSegments.Count; j++)
                        {
                            if (j == i)
                            {
                                continue;
                            }

                            var other = roadSegments[j];
                            if (DistancePointToSegment(endpoint, other.A, other.B) <= boundaryHitTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    blindSegments[i] = !EndpointTouchesAnyOther(roadSegments[i].A) || !EndpointTouchesAnyOther(roadSegments[i].B);
                }

                // Canonical interpretation:
                // a 30.16 boundary is also a 0 hard section boundary when it is
                // west of a 20.11 vertical companion (or south of a 20.11 horizontal companion).
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    var seg = roadSegments[i];
                    if (roadClass[i] != roadClassThirty || !seg.IsUsecLayer)
                    {
                        continue;
                    }

                    var isHardBoundary = false;
                    for (var j = 0; j < roadSegments.Count; j++)
                    {
                        if (i == j || roadClass[j] != roadClassTwenty)
                        {
                            continue;
                        }

                        var companion = roadSegments[j];
                        if ((seg.Horizontal != companion.Horizontal) || (seg.Vertical != companion.Vertical))
                        {
                            continue;
                        }

                        var overlap = Math.Min(seg.MajorMax, companion.MajorMax) - Math.Max(seg.MajorMin, companion.MajorMin);
                        if (overlap < classMinOverlap)
                        {
                            continue;
                        }

                        if (seg.Vertical)
                        {
                            var eastDelta = companion.Axis - seg.Axis;
                            if (eastDelta > 0.10 && Math.Abs(eastDelta - classTwentyGap) <= classTwentyTol)
                            {
                                isHardBoundary = true;
                                break;
                            }
                        }
                        else if (seg.Horizontal)
                        {
                            var northDelta = companion.Axis - seg.Axis;
                            if (northDelta > 0.10 && Math.Abs(northDelta - classTwentyGap) <= classTwentyTol)
                            {
                                isHardBoundary = true;
                                break;
                            }
                        }
                    }

                    generatedThirtyHardAsTwenty[i] = isHardBoundary;
                }

                var roadClassList = roadClass.ToList();
                var blindSegmentList = blindSegments.ToList();
                var generatedThirtyHardAsTwentyList = generatedThirtyHardAsTwenty.ToList();
                const int endpointRoleUnknown = -1;
                const int endpointRoleZero = 0;
                const int endpointRoleTwenty = 20;

                bool TryGetNearestThirtyAxis(bool wantVertical, Point2d referencePoint, out double axis)
                {
                    axis = 0.0;
                    var found = false;
                    var bestDistance = double.MaxValue;
                    const double axisRangeTol = 4.0;

                    for (var i = 0; i < roadSegments.Count; i++)
                    {
                        if (roadClassList[i] != roadClassThirty)
                        {
                            continue;
                        }

                        var candidate = roadSegments[i];
                        if (wantVertical && !candidate.Vertical)
                        {
                            continue;
                        }

                        if (!wantVertical && !candidate.Horizontal)
                        {
                            continue;
                        }

                        var inRange = wantVertical
                            ? (referencePoint.Y >= (candidate.MajorMin - axisRangeTol) && referencePoint.Y <= (candidate.MajorMax + axisRangeTol))
                            : (referencePoint.X >= (candidate.MajorMin - axisRangeTol) && referencePoint.X <= (candidate.MajorMax + axisRangeTol));
                        if (!inRange)
                        {
                            continue;
                        }

                        var d = wantVertical
                            ? Math.Abs(referencePoint.X - candidate.Axis)
                            : Math.Abs(referencePoint.Y - candidate.Axis);
                        if (d >= bestDistance)
                        {
                            continue;
                        }

                        bestDistance = d;
                        axis = candidate.Axis;
                        found = true;
                    }

                    return found;
                }

                int GetEndpointRole(int idx)
                {
                    if (idx < 0 || idx >= roadSegments.Count)
                    {
                        return endpointRoleUnknown;
                    }

                    var seg = roadSegments[idx];
                    var layer = seg.Layer ?? string.Empty;
                    if (seg.IsSecLayer ||
                        string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                    {
                        return endpointRoleZero;
                    }

                    if (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                    {
                        return endpointRoleTwenty;
                    }

                    if (generatedThirtyHardAsTwentyList[idx])
                    {
                        return endpointRoleZero;
                    }

                    if (roadClassList[idx] == roadClassTwenty)
                    {
                        return endpointRoleTwenty;
                    }

                    return endpointRoleUnknown;
                }

                bool IsSameSideOfRoadAllowance(
                    int sourceIndex,
                    Point2d sourcePoint,
                    Point2d sourceOtherPoint,
                    Point2d targetPoint)
                {
                    if (sourceIndex < 0 || sourceIndex >= roadSegments.Count)
                    {
                        return false;
                    }

                    var source = roadSegments[sourceIndex];
                    const double sideTol = 0.25;

                    if (source.Vertical)
                    {
                        if (!TryGetNearestThirtyAxis(wantVertical: false, sourcePoint, out var axisY))
                        {
                            return false;
                        }

                        var sourceSign = SignWithTol(sourcePoint.Y - axisY, sideTol);
                        if (sourceSign == 0)
                        {
                            sourceSign = SignWithTol(sourceOtherPoint.Y - axisY, sideTol);
                        }

                        var targetSign = SignWithTol(targetPoint.Y - axisY, sideTol);
                        if (sourceSign == 0 || targetSign == 0)
                        {
                            return false;
                        }

                        return sourceSign == targetSign;
                    }

                    if (source.Horizontal)
                    {
                        if (!TryGetNearestThirtyAxis(wantVertical: true, sourcePoint, out var axisX))
                        {
                            return false;
                        }

                        var sourceSign = SignWithTol(sourcePoint.X - axisX, sideTol);
                        if (sourceSign == 0)
                        {
                            sourceSign = SignWithTol(sourceOtherPoint.X - axisX, sideTol);
                        }

                        var targetSign = SignWithTol(targetPoint.X - axisX, sideTol);
                        if (sourceSign == 0 || targetSign == 0)
                        {
                            return false;
                        }

                        return sourceSign == targetSign;
                    }

                    return false;
                }

                bool IsAllowedEndpointTarget(
                    int sourceIndex,
                    int targetIndex,
                    out bool usedGeneratedThirtyAsZero,
                    out bool usedBlindFallback)
                {
                    usedGeneratedThirtyAsZero = false;
                    usedBlindFallback = false;

                    var sourceRole = GetEndpointRole(sourceIndex);
                    if (sourceRole != endpointRoleZero && sourceRole != endpointRoleTwenty)
                    {
                        return false;
                    }

                    var targetRole = GetEndpointRole(targetIndex);
                    var targetIsThirtyOnly =
                        roadClassList[targetIndex] == roadClassThirty &&
                        !generatedThirtyHardAsTwentyList[targetIndex] &&
                        !roadSegments[targetIndex].IsSecLayer;

                    if (allowBlindFallback && blindSegmentList[targetIndex] && !targetIsThirtyOnly)
                    {
                        usedBlindFallback = true;
                        if (targetRole == endpointRoleZero &&
                            !roadSegments[targetIndex].IsSecLayer &&
                            generatedThirtyHardAsTwentyList[targetIndex])
                        {
                            usedGeneratedThirtyAsZero = true;
                        }

                        return true;
                    }

                    if (targetRole != sourceRole)
                    {
                        return false;
                    }

                    if (targetRole == endpointRoleZero &&
                        !roadSegments[targetIndex].IsSecLayer &&
                        generatedThirtyHardAsTwentyList[targetIndex])
                    {
                        usedGeneratedThirtyAsZero = true;
                    }

                    return true;
                }

                bool IsOppositeDirectionTarget(int sourceIndex, int targetIndex)
                {
                    if (!TryGetUnitDirection(roadSegments[sourceIndex].A, roadSegments[sourceIndex].B, out var sourceDir))
                    {
                        return false;
                    }

                    if (!TryGetUnitDirection(roadSegments[targetIndex].A, roadSegments[targetIndex].B, out var targetDir))
                    {
                        return false;
                    }

                    // Require near-perpendicular segments.
                    const double maxAbsDot = 0.25; // approx >= 75 degrees apart
                    return Math.Abs(sourceDir.DotProduct(targetDir)) <= maxAbsDot;
                }

                bool HasExistingOppositeConnection(int sourceIndex, Point2d endpoint, Point2d sourceOtherPoint)
                {
                    const double touchTol = 0.35;
                    for (var j = 0; j < roadSegments.Count; j++)
                    {
                        if (j == sourceIndex)
                        {
                            continue;
                        }

                        if (!IsOppositeDirectionTarget(sourceIndex, j))
                        {
                            continue;
                        }

                        if (!IsAllowedEndpointTarget(sourceIndex, j, out _, out _))
                        {
                            continue;
                        }

                        var target = roadSegments[j];
                        if (DistancePointToSegment(endpoint, target.A, target.B) > touchTol)
                        {
                            continue;
                        }

                        if (!IsSameSideOfRoadAllowance(sourceIndex, endpoint, sourceOtherPoint, endpoint))
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool TryExtendSourceEndpoint(int sourceIndex, Point2d oldEndpoint, Point2d newEndpoint)
                {
                    const double endpointTol = 0.35;
                    const double minMove = 0.05;
                    const double collinearTol = 0.35;
                    if (sourceIndex < 0 || sourceIndex >= roadSegments.Count)
                    {
                        return false;
                    }

                    if (newEndpoint.GetDistanceTo(oldEndpoint) <= minMove)
                    {
                        return false;
                    }

                    var source = roadSegments[sourceIndex];
                    if (source.Id.IsNull)
                    {
                        return false;
                    }

                    if (DistancePointToInfiniteLine(newEndpoint, source.A, source.B) > collinearTol)
                    {
                        return false;
                    }

                    if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        return false;
                    }

                    var moved = false;
                    var newA = source.A;
                    var newB = source.B;
                    if (writable is Line ln)
                    {
                        var p0 = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        var p1 = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        var d0 = p0.GetDistanceTo(oldEndpoint);
                        var d1 = p1.GetDistanceTo(oldEndpoint);
                        if (d0 > endpointTol && d1 > endpointTol)
                        {
                            return false;
                        }

                        var moveStart = d0 <= d1;
                        if (moveStart)
                        {
                            ln.StartPoint = new Point3d(newEndpoint.X, newEndpoint.Y, ln.StartPoint.Z);
                            newA = newEndpoint;
                            newB = p1;
                        }
                        else
                        {
                            ln.EndPoint = new Point3d(newEndpoint.X, newEndpoint.Y, ln.EndPoint.Z);
                            newA = p0;
                            newB = newEndpoint;
                        }

                        moved = true;
                    }
                    else if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices >= 2)
                    {
                        var startIndex = 0;
                        var endIndex = pl.NumberOfVertices - 1;
                        var p0 = pl.GetPoint2dAt(startIndex);
                        var p1 = pl.GetPoint2dAt(endIndex);
                        var d0 = p0.GetDistanceTo(oldEndpoint);
                        var d1 = p1.GetDistanceTo(oldEndpoint);
                        if (d0 > endpointTol && d1 > endpointTol)
                        {
                            return false;
                        }

                        var moveStart = d0 <= d1;
                        if (moveStart)
                        {
                            pl.SetPointAt(startIndex, newEndpoint);
                            newA = newEndpoint;
                            newB = p1;
                        }
                        else
                        {
                            pl.SetPointAt(endIndex, newEndpoint);
                            newA = p0;
                            newB = newEndpoint;
                        }

                        moved = true;
                    }

                    if (!moved || newA.GetDistanceTo(newB) <= minMove)
                    {
                        return false;
                    }

                    var horizontal = IsHorizontalLike(newA, newB);
                    var vertical = IsVerticalLike(newA, newB);
                    var axis = horizontal
                        ? 0.5 * (newA.Y + newB.Y)
                        : 0.5 * (newA.X + newB.X);
                    var majorMin = horizontal
                        ? Math.Min(newA.X, newB.X)
                        : Math.Min(newA.Y, newB.Y);
                    var majorMax = horizontal
                        ? Math.Max(newA.X, newB.X)
                        : Math.Max(newA.Y, newB.Y);
                    roadSegments[sourceIndex] = (
                        source.Id,
                        newA,
                        newB,
                        horizontal,
                        vertical,
                        source.IsSecLayer,
                        source.IsUsecLayer,
                        source.Generated,
                        axis,
                        majorMin,
                        majorMax,
                        source.Layer);
                    return true;
                }

                var scannedTwentyEndpoints = 0;
                var alreadyConnected = 0;
                var extended = 0;
                var extendedInPlace = 0;
                var inPlaceRejected = 0;
                var noTarget = 0;
                var roleRejected = 0;
                var sideRejected = 0;
                var maxDistanceRejected = 0;
                var targetDistanceRejected = 0;
                var generatedThirtyAsZeroUsed = 0;
                var blindFallbackUsed = 0;
                var sourceRoleZeroSegments = 0;
                var sourceRoleTwentySegments = 0;
                const double minExtend = 0.05;
                const double maxExtend = 40.0;
                const double targetApparentTol = 40.0;

                var baseSegmentCount = roadSegments.Count;
                for (var i = 0; i < baseSegmentCount; i++)
                {
                    var source = roadSegments[i];
                    var sourceRole = GetEndpointRole(i);
                    if (sourceRole != endpointRoleZero && sourceRole != endpointRoleTwenty)
                    {
                        continue;
                    }

                    // Deterministic ownership: do not drive endpoint extension from horizontal 20.11 sources.
                    // Their joins are resolved by moving the opposing vertical 20.11/0 boundaries.
                    if (sourceRole == endpointRoleTwenty && source.Horizontal)
                    {
                        continue;
                    }

                    if (sourceRole == endpointRoleZero)
                    {
                        sourceRoleZeroSegments++;
                    }
                    else if (sourceRole == endpointRoleTwenty)
                    {
                        sourceRoleTwentySegments++;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        scannedTwentyEndpoints++;
                        var endpoint = endpointIndex == 0 ? source.A : source.B;
                        var other = endpointIndex == 0 ? source.B : source.A;
                        var outward = endpoint - other;
                        if (outward.Length <= 1e-6)
                        {
                            continue;
                        }

                        var outwardUnit = outward / outward.Length;
                        if (HasExistingOppositeConnection(i, endpoint, other))
                        {
                            alreadyConnected++;
                            continue;
                        }

                        var found = false;
                        var bestScore = double.MaxValue;
                        var bestTargetPoint = endpoint;
                        var bestUsedGeneratedThirtyAsZero = false;
                        var bestUsedBlindFallback = false;
                        for (var j = 0; j < roadSegments.Count; j++)
                        {
                            if (j == i)
                            {
                                continue;
                            }

                            var target = roadSegments[j];
                            if (!IsOppositeDirectionTarget(i, j))
                            {
                                continue;
                            }

                            if (!IsAllowedEndpointTarget(i, j, out var usedGeneratedThirtyAsZero, out var usedBlindFallback))
                            {
                                roleRejected++;
                                continue;
                            }

                            if (!TryIntersectInfiniteLines(source.A, source.B, target.A, target.B, out var intersection))
                            {
                                continue;
                            }

                            var along = (intersection - endpoint).DotProduct(outwardUnit);
                            if (along <= minExtend || along > maxExtend)
                            {
                                maxDistanceRejected++;
                                continue;
                            }

                            if (!IsSameSideOfRoadAllowance(i, endpoint, other, intersection))
                            {
                                sideRejected++;
                                continue;
                            }

                            var targetDistance = DistancePointToSegment(intersection, target.A, target.B);
                            if (targetDistance > targetApparentTol)
                            {
                                targetDistanceRejected++;
                                continue;
                            }

                            var score = along + (2.0 * targetDistance);
                            if (score >= bestScore)
                            {
                                continue;
                            }

                            found = true;
                            bestScore = score;
                            bestTargetPoint = intersection;
                            bestUsedGeneratedThirtyAsZero = usedGeneratedThirtyAsZero;
                            bestUsedBlindFallback = usedBlindFallback;
                        }

                        if (!found)
                        {
                            noTarget++;
                            continue;
                        }

                        if (TryExtendSourceEndpoint(i, endpoint, bestTargetPoint))
                        {
                            extended++;
                            extendedInPlace++;
                            if (bestUsedGeneratedThirtyAsZero)
                            {
                                generatedThirtyAsZeroUsed++;
                            }

                            if (bestUsedBlindFallback)
                            {
                                blindFallbackUsed++;
                            }
                            continue;
                        }

                        inPlaceRejected++;
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: canonical endpoint rule scanned={scannedTwentyEndpoints}, sources0={sourceRoleZeroSegments}, sources20={sourceRoleTwentySegments}, alreadyConnected={alreadyConnected}, extended={extended}, inPlace={extendedInPlace}, inPlaceRejected={inPlaceRejected}, noTarget={noTarget}, roleRejected={roleRejected}, sideRejected={sideRejected}, rangeRejected={maxDistanceRejected}, targetDistanceRejected={targetDistanceRejected}, generated30As0Used={generatedThirtyAsZeroUsed}, blindFallbackUsed={blindFallbackUsed}.");
            }
        }

        private static void ConnectUsecBlindSouthwestTwentyTwelveLines(
            Database database,
            IEnumerable<QuarterLabelInfo> labelQuarterInfos,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || labelQuarterInfos == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var targetInfos = labelQuarterInfos
                .Where(info =>
                    info != null &&
                    IsUsecBlindSouthSection(info.SectionKey.Section))
                .ToList();
            if (targetInfos.Count == 0)
            {
                logger?.WriteLine("Cleanup: SW L-USEC 20.11 blind-line connect skipped (no target section info).");
                return;
            }

            var targetSectionIds = targetInfos
                .Where(info => !info.SectionPolylineId.IsNull)
                .Select(info => info.SectionPolylineId)
                .Distinct()
                .ToList();
            var sectionTargets = new List<(ObjectId SectionId, Point2d SwCorner, Vector2d EastUnit, Vector2d NorthUnit, double Width, double Height, Extents3d Window)>();
            var clipWindows = new List<Extents3d>();
            if (targetSectionIds.Count > 0)
            {
                const double swWindowBuffer = 120.0;
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    foreach (var sectionId in targetSectionIds)
                    {
                        if (!(tr.GetObject(sectionId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            var ext = section.GeometricExtents;
                            if (!TryGetQuarterAnchors(section, out var sectionAnchors))
                            {
                                sectionAnchors = GetFallbackAnchors(section);
                            }

                            var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
                            var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));
                            var sectionWidth = sectionAnchors.Left.GetDistanceTo(sectionAnchors.Right);
                            var sectionHeight = sectionAnchors.Bottom.GetDistanceTo(sectionAnchors.Top);
                            if (sectionWidth <= 1e-6)
                            {
                                sectionWidth = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X);
                            }

                            if (sectionHeight <= 1e-6)
                            {
                                sectionHeight = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y);
                            }

                            Point2d swCorner;
                            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out swCorner))
                            {
                                swCorner = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                            }

                            var midX = 0.5 * (ext.MinPoint.X + ext.MaxPoint.X);
                            var midY = 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y);
                            var swWindow = new Extents3d(
                                new Point3d(ext.MinPoint.X - swWindowBuffer, ext.MinPoint.Y - swWindowBuffer, 0.0),
                                new Point3d(midX + swWindowBuffer, midY + swWindowBuffer, 0.0));
                            clipWindows.Add(swWindow);
                            sectionTargets.Add((sectionId, swCorner, eastUnit, northUnit, sectionWidth, sectionHeight, swWindow));
                        }
                        catch
                        {
                        }
                    }

                    tr.Commit();
                }
            }

            if (clipWindows.Count == 0 || sectionTargets.Count == 0)
            {
                logger?.WriteLine("Cleanup: SW L-USEC 20.11 blind-line connect skipped (no clip windows).");
                return;
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
                    if (pl.Closed || pl.NumberOfVertices != 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
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

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                bool IsPointInWindow(Point2d p, Extents3d window)
                {
                    return p.X >= window.MinPoint.X && p.X <= window.MaxPoint.X &&
                           p.Y >= window.MinPoint.Y && p.Y <= window.MaxPoint.Y;
                }

                bool DoesSegmentIntersectWindow(Point2d a, Point2d b, Extents3d window)
                {
                    return IsPointInWindow(a, window) ||
                           IsPointInWindow(b, window) ||
                           TryClipSegmentToWindow(a, b, window, out _, out _);
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

                // Ownership note for 30.16/20.11 logic:
                // - west-side road allowances belong to the section on their west (left)
                // - south-side road allowances belong to the section above (north)
                // so this pass gathers nearby sec/usec segments from both generated and existing geometry.
                var roadAllowanceSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Generated, bool IsUsecLayer)>();
                var hardConnectorSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add((id, a, b));
                        }

                        continue;
                    }

                    var layerName = ent.Layer ?? string.Empty;
                    var isSecLayer = string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    var isUsecLayer = IsUsecLayer(layerName);
                    if (!isSecLayer && !isUsecLayer)
                    {
                        continue;
                    }

                    if (isSecLayer ||
                        string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                    {
                        hardConnectorSegments.Add((id, a, b));
                    }

                    roadAllowanceSegments.Add((id, a, b, generatedSet.Contains(id), isUsecLayer));
                }

                if (roadAllowanceSegments.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine("Cleanup: SW L-USEC 20.11 blind-line connect skipped (no generated RA segments).");
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double primaryExpectedOffset = 20.11;
                const double secondaryExpectedOffset = CorrectionLinePairGapMeters;
                const double offsetTol = 3.0;
                const double maxEndpointGap = 70.0;
                var usedHorizontalSegments = new HashSet<ObjectId>();
                var connectedPairs = 0;
                var movedHorizontalEndpoints = 0;
                var movedVerticalEndpoints = 0;
                var sectionsWithCandidates = 0;
                var pairCandidatesEvaluated = 0;
                var forcedCornerAttempts = 0;
                var forcedCornerConnected = 0;
                var lsdMidpointAdjustments = new List<(ObjectId SectionId, Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var lsdVerticalMidpointAdjustments = new List<(ObjectId SectionId, Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var lsdFallbackEndpointAdjustments = 0;
                const bool allowSwBlindLineLsdMidpointAdjustments = false;

                for (var si = 0; si < sectionTargets.Count; si++)
                {
                    var sectionTarget = sectionTargets[si];
                    var swCorner = sectionTarget.SwCorner;
                    var eastUnit = sectionTarget.EastUnit;
                    var northUnit = sectionTarget.NorthUnit;

                    double ToU(Point2d p) => (p - swCorner).DotProduct(eastUnit);
                    double ToV(Point2d p) => (p - swCorner).DotProduct(northUnit);
                    bool EndpointTouchesHardConnector(Point2d endpoint, ObjectId sourceId)
                    {
                        const double connectorTol = 0.35;
                        for (var ci = 0; ci < hardConnectorSegments.Count; ci++)
                        {
                            var c = hardConnectorSegments[ci];
                            if (c.Id == sourceId)
                            {
                                continue;
                            }

                            if (DistancePointToSegment(endpoint, c.A, c.B) <= connectorTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                    (
                        List<(ObjectId Id, bool IsGenerated, bool IsUsecLayer, Point2d WestPoint, Point2d EastPoint, bool WestIsStart, double VLine, double WestU, double WestV)> Horizontals,
                        List<(ObjectId Id, bool IsGenerated, bool IsUsecLayer, Point2d SouthPoint, Point2d NorthPoint, bool SouthIsStart, double ULine, double SouthU, double SouthV)> Verticals
                    )
                    CollectCandidatesForOffset(double expectedOffsetForBand)
                    {
                        var horizontalsForOffset = new List<(ObjectId Id, bool IsGenerated, bool IsUsecLayer, Point2d WestPoint, Point2d EastPoint, bool WestIsStart, double VLine, double WestU, double WestV)>();
                        var verticalsForOffset = new List<(ObjectId Id, bool IsGenerated, bool IsUsecLayer, Point2d SouthPoint, Point2d NorthPoint, bool SouthIsStart, double ULine, double SouthU, double SouthV)>();
                        for (var gi = 0; gi < roadAllowanceSegments.Count; gi++)
                        {
                            var seg = roadAllowanceSegments[gi];
                            if (usedHorizontalSegments.Contains(seg.Id))
                            {
                                continue;
                            }

                            if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                            {
                                continue;
                            }

                            var d = seg.B - seg.A;
                            var len = d.Length;
                            if (len <= 1e-6)
                            {
                                continue;
                            }

                            var uA = ToU(seg.A);
                            var vA = ToV(seg.A);
                            var uB = ToU(seg.B);
                            var vB = ToV(seg.B);
                            var eastComp = Math.Abs(d.DotProduct(eastUnit));
                            var northComp = Math.Abs(d.DotProduct(northUnit));
                            if (eastComp >= northComp)
                            {
                                var westIsStart = uA <= uB;
                                var westPoint = westIsStart ? seg.A : seg.B;
                                var eastPoint = westIsStart ? seg.B : seg.A;
                                var westU = westIsStart ? uA : uB;
                                var westV = westIsStart ? vA : vB;
                                var vLine = 0.5 * (vA + vB);
                                if (Math.Abs(Math.Abs(vLine) - expectedOffsetForBand) > offsetTol)
                                {
                                    continue;
                                }

                                horizontalsForOffset.Add((seg.Id, seg.Generated, seg.IsUsecLayer, westPoint, eastPoint, westIsStart, vLine, westU, westV));
                            }
                            else
                            {
                                var southIsStart = vA <= vB;
                                var southPoint = southIsStart ? seg.A : seg.B;
                                var northPoint = southIsStart ? seg.B : seg.A;
                                var southU = southIsStart ? uA : uB;
                                var southV = southIsStart ? vA : vB;
                                var uLine = 0.5 * (uA + uB);
                                if (Math.Abs(Math.Abs(uLine) - expectedOffsetForBand) > offsetTol)
                                {
                                    continue;
                                }

                                verticalsForOffset.Add((seg.Id, seg.Generated, seg.IsUsecLayer, southPoint, northPoint, southIsStart, uLine, southU, southV));
                            }
                        }

                        return (horizontalsForOffset, verticalsForOffset);
                    }

                    var activeExpectedOffset = primaryExpectedOffset;
                    var primaryCandidates = CollectCandidatesForOffset(primaryExpectedOffset);
                    var horizontals = primaryCandidates.Horizontals;
                    var verticals = primaryCandidates.Verticals;

                    if (horizontals.Count == 0)
                    {
                        var secondaryCandidates = CollectCandidatesForOffset(secondaryExpectedOffset);
                        if (secondaryCandidates.Horizontals.Count == 0)
                        {
                            continue;
                        }

                        activeExpectedOffset = secondaryExpectedOffset;
                        horizontals = secondaryCandidates.Horizontals;
                        verticals = secondaryCandidates.Verticals;
                    }
                    else if (verticals.Count == 0)
                    {
                        var secondaryCandidates = CollectCandidatesForOffset(secondaryExpectedOffset);
                        if (secondaryCandidates.Horizontals.Count > 0 && secondaryCandidates.Verticals.Count > 0)
                        {
                            activeExpectedOffset = secondaryExpectedOffset;
                            horizontals = secondaryCandidates.Horizontals;
                            verticals = secondaryCandidates.Verticals;
                        }
                    }

                    if (verticals.Count == 0)
                    {
                        // Sparse/single-section builds can miss the vertical SW corner leg.
                        // Keep LSD south-end adjustment alive by synthesizing the source (old) segment
                        // one 10.06m band north of the generated 20.11 horizontal.
                        sectionsWithCandidates++;
                        var bestFallbackHorizontal = horizontals
                            .OrderBy(h => Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset))
                            .ThenByDescending(h => h.WestPoint.GetDistanceTo(h.EastPoint))
                            .First();
                        var oldWest = bestFallbackHorizontal.WestPoint + (northUnit * activeExpectedOffset);
                        var oldEast = bestFallbackHorizontal.EastPoint + (northUnit * activeExpectedOffset);
                        lsdMidpointAdjustments.Add((
                            sectionTarget.SectionId,
                            oldWest,
                            oldEast,
                            Midpoint(oldWest, oldEast),
                            Midpoint(bestFallbackHorizontal.WestPoint, bestFallbackHorizontal.EastPoint)));
                        continue;
                    }

                    sectionsWithCandidates++;
                    const double swCornerBand = 190.0;
                    var forcedHorizontalPool = horizontals
                        .Where(h => Math.Min(
                            h.WestPoint.GetDistanceTo(swCorner),
                            h.EastPoint.GetDistanceTo(swCorner)) <= swCornerBand)
                        .ToList();
                    if (forcedHorizontalPool.Count == 0)
                    {
                        forcedHorizontalPool = horizontals;
                    }
                    var forcedUsecHorizontals = forcedHorizontalPool
                        .Where(h => h.IsUsecLayer)
                        .ToList();
                    if (forcedUsecHorizontals.Count > 0)
                    {
                        forcedHorizontalPool = forcedUsecHorizontals;
                    }
                    var forcedGeneratedHorizontals = forcedHorizontalPool
                        .Where(h => h.IsGenerated)
                        .ToList();
                    if (forcedGeneratedHorizontals.Count > 0)
                    {
                        forcedHorizontalPool = forcedGeneratedHorizontals;
                    }

                    var forcedVerticalPool = verticals
                        .Where(v => Math.Min(
                            v.SouthPoint.GetDistanceTo(swCorner),
                            v.NorthPoint.GetDistanceTo(swCorner)) <= swCornerBand)
                        .ToList();
                    if (forcedVerticalPool.Count == 0)
                    {
                        forcedVerticalPool = verticals;
                    }
                    var forcedUsecVerticals = forcedVerticalPool
                        .Where(v => v.IsUsecLayer)
                        .ToList();
                    if (forcedUsecVerticals.Count > 0)
                    {
                        forcedVerticalPool = forcedUsecVerticals;
                    }
                    var forcedGeneratedVerticals = forcedVerticalPool
                        .Where(v => v.IsGenerated)
                        .ToList();
                    if (forcedGeneratedVerticals.Count > 0)
                    {
                        forcedVerticalPool = forcedGeneratedVerticals;
                    }

                    if (forcedHorizontalPool.Count > 0 && forcedVerticalPool.Count > 0)
                    {
                        var forcedHorizontal = forcedHorizontalPool
                            .OrderBy(h => Math.Min(
                                h.WestPoint.GetDistanceTo(swCorner),
                                h.EastPoint.GetDistanceTo(swCorner)))
                            .ThenByDescending(h => h.IsUsecLayer)
                            .ThenByDescending(h => h.IsGenerated)
                            .ThenBy(h => Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset))
                            .ThenBy(h => h.WestPoint.GetDistanceTo(h.EastPoint))
                            .First();
                        var forcedVertical = forcedVerticalPool
                            .OrderBy(v => Math.Min(
                                v.SouthPoint.GetDistanceTo(swCorner),
                                v.NorthPoint.GetDistanceTo(swCorner)))
                            .ThenByDescending(v => v.IsUsecLayer)
                            .ThenByDescending(v => v.IsGenerated)
                            .ThenBy(v => Math.Abs(Math.Abs(v.ULine) - activeExpectedOffset))
                            .ThenBy(v => v.SouthPoint.GetDistanceTo(v.NorthPoint))
                            .First();
                        var forcedTarget = swCorner + (eastUnit * forcedVertical.ULine) + (northUnit * forcedHorizontal.VLine);
                        if (TryIntersectInfiniteLines(
                            forcedHorizontal.WestPoint,
                            forcedHorizontal.EastPoint,
                            forcedVertical.SouthPoint,
                            forcedVertical.NorthPoint,
                            out var forcedIntersection))
                        {
                            forcedTarget = forcedIntersection;
                        }

                        if (IsPointInWindow(forcedTarget, sectionTarget.Window) ||
                            forcedHorizontal.WestPoint.GetDistanceTo(forcedTarget) <= maxEndpointGap ||
                            forcedVertical.SouthPoint.GetDistanceTo(forcedTarget) <= maxEndpointGap)
                        {
                            forcedCornerAttempts++;
                            // SW rule: only pull the corner-facing endpoints.
                            var forceMoveHStart = forcedHorizontal.WestIsStart;
                            var fixedHPoint = forcedHorizontal.EastPoint;
                            var forceHMove = forcedHorizontal.WestPoint.GetDistanceTo(forcedTarget);

                            var forceMoveVStart = forcedVertical.SouthIsStart;
                            var fixedVPoint = forcedVertical.NorthPoint;
                            var forceVMove = forcedVertical.SouthPoint.GetDistanceTo(forcedTarget);

                            // Strict SW rule: move only vertical 20.11 endpoint; never extend horizontal here.
                            var allowForceH = false;
                            var allowForceV = forcedVertical.IsUsecLayer && forceVMove <= maxEndpointGap;
                            if (allowForceV &&
                                (EndpointTouchesHardConnector(forcedVertical.SouthPoint, forcedVertical.Id) ||
                                 ToV(forcedTarget) > (forcedVertical.SouthV + 0.25)))
                            {
                                allowForceV = false;
                            }

                            if (allowForceV)
                            {
                                Entity? forcedHWritable = null;
                                if (allowForceH)
                                {
                                    forcedHWritable = tr.GetObject(forcedHorizontal.Id, OpenMode.ForWrite, false) as Entity;
                                    if (forcedHWritable == null || forcedHWritable.IsErased)
                                    {
                                        allowForceH = false;
                                    }
                                }

                                Entity? forcedVWritable = null;
                                if (allowForceV)
                                {
                                    forcedVWritable = tr.GetObject(forcedVertical.Id, OpenMode.ForWrite, false) as Entity;
                                    if (forcedVWritable == null || forcedVWritable.IsErased)
                                    {
                                        allowForceV = false;
                                    }
                                }

                                var movedForcedV = allowForceV &&
                                                   forcedVWritable != null &&
                                                   TryMoveEndpoint(forcedVWritable, forceMoveVStart, forcedTarget, endpointMoveTol);
                                var movedForcedH = allowForceH &&
                                                   forcedHWritable != null &&
                                                   TryMoveEndpoint(forcedHWritable, forceMoveHStart, forcedTarget, endpointMoveTol);
                                if (movedForcedH || movedForcedV)
                                {
                                    forcedCornerConnected++;
                                    connectedPairs++;
                                    if (movedForcedH)
                                    {
                                        usedHorizontalSegments.Add(forcedHorizontal.Id);
                                        movedHorizontalEndpoints++;
                                        lsdMidpointAdjustments.Add((
                                            sectionTarget.SectionId,
                                            forcedHorizontal.WestPoint,
                                            forcedHorizontal.EastPoint,
                                            Midpoint(forcedHorizontal.WestPoint, forcedHorizontal.EastPoint),
                                            Midpoint(forcedTarget, fixedHPoint)));
                                    }

                                    if (movedForcedV)
                                    {
                                        movedVerticalEndpoints++;
                                        lsdVerticalMidpointAdjustments.Add((
                                            sectionTarget.SectionId,
                                            forcedVertical.SouthPoint,
                                            forcedVertical.NorthPoint,
                                            Midpoint(forcedVertical.SouthPoint, forcedVertical.NorthPoint),
                                            Midpoint(forcedTarget, fixedVPoint)));
                                    }

                                    // Deterministic SW 20.11 L-corner handled for this section.
                                    continue;
                                }
                            }
                        }
                    }

                    var pairCandidates = new List<(
                        ObjectId HId,
                        Point2d HOldMoved,
                        Point2d HOldFixed,
                        bool HMoveStart,
                        double HMove,
                        ObjectId VId,
                        Point2d VOldMoved,
                        Point2d VOldFixed,
                        bool VMoveStart,
                        double VMove,
                        bool MoveHorizontal,
                        bool MoveVertical,
                        Point2d Target,
                        double Score)>();
                    for (var hi = 0; hi < horizontals.Count; hi++)
                    {
                        var h = horizontals[hi];
                        for (var vi = 0; vi < verticals.Count; vi++)
                        {
                            var v = verticals[vi];
                            if (h.Id == v.Id)
                            {
                                continue;
                            }

                            // Keep pairing local to the section SW corner; avoid long-range snaps.
                            var hCornerDist = Math.Min(
                                h.WestPoint.GetDistanceTo(swCorner),
                                h.EastPoint.GetDistanceTo(swCorner));
                            var vCornerDist = Math.Min(
                                v.SouthPoint.GetDistanceTo(swCorner),
                                v.NorthPoint.GetDistanceTo(swCorner));
                            if (hCornerDist > swCornerBand || vCornerDist > swCornerBand)
                            {
                                continue;
                            }

                            // Allow non-generated pairs here: after trim/layer normalization the open-T
                            // counterpart can be non-generated. Movement is still restricted to L-USEC
                            // endpoints by canMoveHorizontal/canMoveVertical below.
                            if (!h.IsUsecLayer && !v.IsUsecLayer)
                            {
                                continue;
                            }

                            var target = swCorner + (eastUnit * v.ULine) + (northUnit * h.VLine);
                            if (!IsPointInWindow(target, sectionTarget.Window))
                            {
                                continue;
                            }

                            var hMoveStart = h.WestIsStart;
                            var hOldMoved = h.WestPoint;
                            var hOldFixed = h.EastPoint;
                            var hMove = h.WestPoint.GetDistanceTo(target);

                            // Accept long vertical 20.11 lines by evaluating against the vertical segment,
                            // not the south endpoint distance.
                            var vDistance = DistancePointToSegment(target, v.SouthPoint, v.NorthPoint);
                            if (vDistance > 3.5)
                            {
                                continue;
                            }

                            var vMoveStart = v.SouthIsStart;
                            var vOldMoved = v.SouthPoint;
                            var vOldFixed = v.NorthPoint;
                            var vMove = v.SouthPoint.GetDistanceTo(target);
                            var targetV = ToV(target);
                            if (targetV > (v.SouthV + 0.25))
                            {
                                continue;
                            }

                            // Strict SW rule: move vertical 20.11 endpoint only, and only if dangling.
                            var canMoveHorizontal = false;
                            var canMoveVertical =
                                v.IsUsecLayer &&
                                vMove <= maxEndpointGap &&
                                !EndpointTouchesHardConnector(vOldMoved, v.Id);
                            if (!canMoveHorizontal && !canMoveVertical)
                            {
                                continue;
                            }

                            // Deterministic SW 20.11 L-corner rule: move the vertical endpoint
                            // to the computed 20.11 intersection.
                            var moveHorizontal = canMoveHorizontal;
                            var moveVertical = canMoveVertical;
                            var chosenMove = (moveHorizontal ? hMove : 0.0) + (moveVertical ? vMove : 0.0);
                            var cornerGap = h.WestPoint.GetDistanceTo(v.SouthPoint);

                            var score =
                                cornerGap +
                                chosenMove +
                                vDistance +
                                Math.Abs(Math.Abs(v.ULine) - activeExpectedOffset) +
                                Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset);
                            pairCandidates.Add((
                                h.Id,
                                hOldMoved,
                                hOldFixed,
                                hMoveStart,
                                hMove,
                                v.Id,
                                vOldMoved,
                                vOldFixed,
                                vMoveStart,
                                vMove,
                                moveHorizontal,
                                moveVertical,
                                target,
                                score));
                        }
                    }

                    pairCandidatesEvaluated += pairCandidates.Count;
                    if (pairCandidates.Count == 0)
                    {
                        var bestFallbackHorizontal = horizontals
                            .OrderBy(h => Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset))
                            .ThenByDescending(h => h.WestPoint.GetDistanceTo(h.EastPoint))
                            .First();
                        var oldWest = bestFallbackHorizontal.WestPoint + (northUnit * activeExpectedOffset);
                        var oldEast = bestFallbackHorizontal.EastPoint + (northUnit * activeExpectedOffset);
                        lsdMidpointAdjustments.Add((
                            sectionTarget.SectionId,
                            oldWest,
                            oldEast,
                            Midpoint(oldWest, oldEast),
                            Midpoint(bestFallbackHorizontal.WestPoint, bestFallbackHorizontal.EastPoint)));
                        continue;
                    }

                    var orderedCandidates = pairCandidates
                        .OrderBy(c => c.Score)
                        .ToList();
                    for (var ci = 0; ci < orderedCandidates.Count; ci++)
                    {
                        var candidate = orderedCandidates[ci];
                        if (usedHorizontalSegments.Contains(candidate.HId))
                        {
                            continue;
                        }

                        Entity? hWritable = null;
                        if (candidate.MoveHorizontal)
                        {
                            hWritable = tr.GetObject(candidate.HId, OpenMode.ForWrite, false) as Entity;
                            if (hWritable == null || hWritable.IsErased)
                            {
                                continue;
                            }
                        }

                        Entity? vWritable = null;
                        if (candidate.MoveVertical)
                        {
                            vWritable = tr.GetObject(candidate.VId, OpenMode.ForWrite, false) as Entity;
                            if (vWritable == null || vWritable.IsErased)
                            {
                                continue;
                            }
                        }

                        var movedV = candidate.MoveVertical &&
                                     vWritable != null &&
                                     TryMoveEndpoint(vWritable, candidate.VMoveStart, candidate.Target, endpointMoveTol);
                        var movedH = candidate.MoveHorizontal &&
                                     hWritable != null &&
                                     TryMoveEndpoint(hWritable, candidate.HMoveStart, candidate.Target, endpointMoveTol);
                        if (!movedH && !movedV)
                        {
                            continue;
                        }

                        if (movedH)
                        {
                            usedHorizontalSegments.Add(candidate.HId);
                        }
                        if (movedH || movedV)
                        {
                            connectedPairs++;
                        }
                        if (movedH)
                        {
                            movedHorizontalEndpoints++;
                            lsdMidpointAdjustments.Add((
                                sectionTarget.SectionId,
                                candidate.HOldMoved,
                                candidate.HOldFixed,
                                Midpoint(candidate.HOldMoved, candidate.HOldFixed),
                                Midpoint(candidate.Target, candidate.HOldFixed)));
                        }

                        if (movedV)
                        {
                            movedVerticalEndpoints++;
                            lsdVerticalMidpointAdjustments.Add((
                                sectionTarget.SectionId,
                                candidate.VOldMoved,
                                candidate.VOldFixed,
                                Midpoint(candidate.VOldMoved, candidate.VOldFixed),
                                Midpoint(candidate.Target, candidate.VOldFixed)));
                        }

                        // One corner-connection per target section is enough.
                        break;
                    }
                }

                var lsdAdjusted = 0;
                if (allowSwBlindLineLsdMidpointAdjustments &&
                    (lsdMidpointAdjustments.Count > 0 || lsdVerticalMidpointAdjustments.Count > 0) &&
                    lsdSegments.Count > 0)
                {
                    List<(ObjectId SectionId, Point2d SwCorner, Vector2d EastUnit, Vector2d NorthUnit, double Width, double Height, Extents3d Window)> owningSectionTargets = sectionTargets!;
                    List<(ObjectId SectionId, Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)> horizontalMidpointAdjustments = lsdMidpointAdjustments!;
                    List<(ObjectId SectionId, Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)> verticalMidpointAdjustments = lsdVerticalMidpointAdjustments!;
                    const double lsdOldSegmentTol = 2.0;
                    const double lsdOldMidpointTol = 45.0;
                    const double lsdFallbackOldMidpointTol = 90.0;
                    const double lsdMaxMove = 80.0;
                    const double southwardTol = 0.50;
                    const double maxSouthwardDelta = 28.0;
                    const double eastwardTol = 1.0;
                    const double maxWestwardDelta = 70.0;
                    const double ownershipUTol = 40.0;
                    const double ownershipVTol = 55.0;

                    bool TryGetOwningSectionIndex(Point2d a, Point2d b, out int sectionIndex)
                    {
                        sectionIndex = -1;
                        var mid = Midpoint(a, b);
                        var bestDistance = double.MaxValue;
                        for (var si = 0; si < owningSectionTargets.Count; si++)
                        {
                            var sectionTarget = owningSectionTargets[si];
                            if (!DoesSegmentIntersectWindow(a, b, sectionTarget.Window) &&
                                !IsPointInWindow(mid, sectionTarget.Window))
                            {
                                continue;
                            }

                            var midU = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            if (midU < -ownershipUTol || midU > (sectionTarget.Width + ownershipUTol))
                            {
                                continue;
                            }

                            var midV = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            if (midV < -(primaryExpectedOffset + ownershipVTol) || midV > (sectionTarget.Height + ownershipVTol))
                            {
                                continue;
                            }

                            var d = mid.GetDistanceTo(sectionTarget.SwCorner);
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                sectionIndex = si;
                            }
                        }

                        return sectionIndex >= 0;
                    }

                    bool TrySelectSouthTargetMidpoint(
                        Point2d southEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint,
                        out bool usedFallback)
                    {
                        targetMidpoint = southEndpoint;
                        usedFallback = false;
                        var sectionTarget = owningSectionTargets[sectionIndex];
                        var endpointV = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);

                        var found = false;
                        var bestSouthDelta = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < horizontalMidpointAdjustments.Count; i++)
                        {
                            var adj = horizontalMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var segDistance = DistancePointToSegment(southEndpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var oldMidDistance = southEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var targetV = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var southDelta = endpointV - targetV;
                            if (southDelta < -southwardTol || southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            var move = southEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Fallback for noisy exploded LSD geometry: same section only, still southward-only.
                        for (var i = 0; i < horizontalMidpointAdjustments.Count; i++)
                        {
                            var adj = horizontalMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var oldMidDistance = southEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdFallbackOldMidpointTol)
                            {
                                continue;
                            }

                            var targetV = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var southDelta = endpointV - targetV;
                            if (southDelta < -southwardTol || southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            var move = southEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Strict: do not use neighboring-section nearest fallback.
                        // Only allow same-section midpoint mappings.
                        return false;
                    }

                    bool TrySelectWestTargetMidpoint(
                        Point2d westEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint,
                        out bool usedFallback)
                    {
                        targetMidpoint = westEndpoint;
                        usedFallback = false;
                        var sectionTarget = owningSectionTargets[sectionIndex];
                        var endpointU = (westEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);

                        var found = false;
                        var bestSegDistance = double.MaxValue;
                        var bestMidDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < verticalMidpointAdjustments.Count; i++)
                        {
                            var adj = verticalMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var segDistance = DistancePointToSegment(westEndpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var oldMidDistance = westEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var targetU = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var westDelta = endpointU - targetU;
                            if (westDelta < -eastwardTol || westDelta > maxWestwardDelta)
                            {
                                continue;
                            }

                            var move = westEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && oldMidDistance < (bestMidDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(oldMidDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && move < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestSegDistance = segDistance;
                            bestMidDistance = oldMidDistance;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        if (found)
                        {
                            return true;
                        }

                        for (var i = 0; i < verticalMidpointAdjustments.Count; i++)
                        {
                            var adj = verticalMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var oldMidDistance = westEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdFallbackOldMidpointTol)
                            {
                                continue;
                            }

                            var targetU = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var westDelta = endpointU - targetU;
                            if (westDelta < -eastwardTol || westDelta > maxWestwardDelta)
                            {
                                continue;
                            }

                            var move = westEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterMid = oldMidDistance < (bestMidDistance - 1e-6);
                            var tiedMid = Math.Abs(oldMidDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && move < bestMoveDistance;
                            if (!betterMid && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestMidDistance = oldMidDistance;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        return found;
                    }

                    for (var i = 0; i < lsdSegments.Count; i++)
                    {
                        var lsd = lsdSegments[i];
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        if (!TryGetOwningSectionIndex(p0, p1, out var sectionIndex))
                        {
                            continue;
                        }

                        var sectionTarget = sectionTargets[sectionIndex];
                        var moveStart = false;
                        var targetMid = default(Point2d);
                        var usedFallback = false;
                        if (IsVerticalLike(p0, p1))
                        {
                            var v0 = (p0 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var v1 = (p1 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            moveStart = v0 <= v1;
                            var southEndpoint = moveStart ? p0 : p1;
                            if (!TrySelectSouthTargetMidpoint(
                                southEndpoint,
                                sectionIndex,
                                out targetMid,
                                out usedFallback))
                            {
                                continue;
                            }
                        }
                        else if (IsHorizontalLike(p0, p1))
                        {
                            var u0 = (p0 - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var u1 = (p1 - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            moveStart = u0 <= u1;
                            var westEndpoint = moveStart ? p0 : p1;
                            if (!TrySelectWestTargetMidpoint(
                                westEndpoint,
                                sectionIndex,
                                out targetMid,
                                out usedFallback))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            lsdPoly.SetPointAt(moveStart ? 0 : 1, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                        if (usedFallback)
                        {
                            lsdFallbackEndpointAdjustments++;
                        }
                    }
                }

                tr.Commit();
                if (connectedPairs > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: connected {connectedPairs} SW blind-line 20.11 pair(s) " +
                        $"(H={movedHorizontalEndpoints}, V={movedVerticalEndpoints}).");
                    logger?.WriteLine(
                        $"Cleanup: SW 20.11 forced-corner attempts={forcedCornerAttempts}, connected={forcedCornerConnected}.");
                    if (lsdAdjusted > 0)
                    {
                        logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of SW L-USEC 20.11 blind-line connection(s).");
                        if (lsdFallbackEndpointAdjustments > 0)
                        {
                            logger?.WriteLine($"Cleanup: {lsdFallbackEndpointAdjustments} SW L-SECTION-LSD endpoint(s) used same-section relaxed SW blind-line midpoint fallback.");
                        }
                    }
                }
                else
                {
                    logger?.WriteLine(
                        $"Cleanup: SW L-USEC 20.11 blind-line connect found no candidates " +
                        $"(sections={sectionTargets.Count}, withHV={sectionsWithCandidates}, pairs={pairCandidatesEvaluated}).");
                }
            }
        }

        private static void ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(
            Database database,
            IReadOnlyList<string> searchFolders,
            IEnumerable<QuarterLabelInfo> labelQuarterInfos,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            // TODO(2026-02-09): SE L-USEC east-boundary extension still misses some cases;
            // revisit boundary candidate selection and downward target-horizontal matching.
            if (database == null ||
                searchFolders == null ||
                labelQuarterInfos == null ||
                generatedRoadAllowanceIds == null ||
                generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var targetInfos = labelQuarterInfos
                .Where(info =>
                    info != null &&
                    IsUsecSeSouthSection(info.SectionKey.Section))
                .ToList();
            if (targetInfos.Count == 0)
            {
                logger?.WriteLine("Cleanup: SE L-USEC south 20.11 connect skipped (no target section info).");
                return;
            }

            var targetSectionIds = targetInfos
                .Where(info => !info.SectionPolylineId.IsNull)
                .Select(info => info.SectionPolylineId)
                .Distinct()
                .ToList();
            var sectionKeyById = targetInfos
                .Where(info => info != null && !info.SectionPolylineId.IsNull)
                .GroupBy(info => info.SectionPolylineId)
                .ToDictionary(g => g.Key, g => g.First().SectionKey);

            var sectionTargets = new List<(ObjectId SectionId, Point2d SwCorner, Vector2d EastUnit, Vector2d NorthUnit, Extents3d Window, double EastEdgeU, Point2d OriginalSeCorner, bool HasOriginalSeCorner)>();
            var clipWindows = new List<Extents3d>();
            if (targetSectionIds.Count > 0)
            {
                const double seWindowBuffer = 120.0;
                var outlineLogger = logger ?? new Logger();
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    foreach (var sectionId in targetSectionIds)
                    {
                        if (!(tr.GetObject(sectionId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            var ext = section.GeometricExtents;
                            if (!TryGetQuarterAnchors(section, out var sectionAnchors))
                            {
                                sectionAnchors = GetFallbackAnchors(section);
                            }

                            var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
                            var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));
                            Point2d swCorner;
                            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out swCorner))
                            {
                                swCorner = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                            }

                            Point2d seCorner;
                            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out seCorner))
                            {
                                seCorner = new Point2d(ext.MaxPoint.X, ext.MinPoint.Y);
                            }

                            var eastEdgeU = (seCorner - swCorner).DotProduct(eastUnit);
                            if (eastEdgeU <= 1e-6)
                            {
                                eastEdgeU = 0.0;
                                for (var vi = 0; vi < section.NumberOfVertices; vi++)
                                {
                                    var u = (section.GetPoint2dAt(vi) - swCorner).DotProduct(eastUnit);
                                    if (u > eastEdgeU)
                                    {
                                        eastEdgeU = u;
                                    }
                                }
                            }

                            var originalSeCorner = seCorner;
                            var hasOriginalSeCorner = false;
                            if (sectionKeyById.TryGetValue(sectionId, out var sectionKey) &&
                                TryLoadSectionOutline(searchFolders, sectionKey, outlineLogger, out var outline))
                            {
                                var sectionNumber = ParseSectionNumber(sectionKey.Section);
                                if (TryCreateSectionSpatialInfo(outline, sectionNumber, out var spatialInfo) &&
                                    spatialInfo != null)
                                {
                                    try
                                    {
                                        originalSeCorner = spatialInfo.SouthWest + (spatialInfo.EastUnit * spatialInfo.Width);
                                        hasOriginalSeCorner = true;
                                    }
                                    finally
                                    {
                                        spatialInfo.SectionPolyline.Dispose();
                                    }
                                }
                            }

                            var midX = 0.5 * (ext.MinPoint.X + ext.MaxPoint.X);
                            var midY = 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y);
                            var seWindow = new Extents3d(
                                new Point3d(midX - seWindowBuffer, ext.MinPoint.Y - seWindowBuffer, 0.0),
                                new Point3d(ext.MaxPoint.X + seWindowBuffer, midY + seWindowBuffer, 0.0));
                            clipWindows.Add(seWindow);
                            sectionTargets.Add((sectionId, swCorner, eastUnit, northUnit, seWindow, eastEdgeU, originalSeCorner, hasOriginalSeCorner));
                        }
                        catch
                        {
                        }
                    }

                    tr.Commit();
                }
            }

            if (clipWindows.Count == 0 || sectionTargets.Count == 0)
            {
                logger?.WriteLine("Cleanup: SE L-USEC south 20.11 connect skipped (no clip windows).");
                return;
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
                    if (pl.Closed || pl.NumberOfVertices != 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
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

                if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                {
                    var old = pl.GetPoint2dAt(moveStart ? 0 : 1);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(moveStart ? 0 : 1, target);
                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                bool IsPointInWindow(Point2d p, Extents3d window)
                {
                    return p.X >= window.MinPoint.X && p.X <= window.MaxPoint.X &&
                           p.Y >= window.MinPoint.Y && p.Y <= window.MaxPoint.Y;
                }

                bool DoesSegmentIntersectWindow(Point2d a, Point2d b, Extents3d window)
                {
                    return IsPointInWindow(a, window) ||
                           IsPointInWindow(b, window) ||
                           TryClipSegmentToWindow(a, b, window, out _, out _);
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

                var usecHorizontals = new List<(ObjectId Id, Point2d A, Point2d B, bool IsGenerated, bool IsUsecTwentyLayer, bool IsUsecBaseLayer)>();
                var originalVerticals = new List<(ObjectId Id, Point2d A, Point2d B, bool IsSecLayer, bool IsGenerated, bool IsUsecZeroLayer)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isUsecZeroLayer = string.Equals(ent.Layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase);
                    var isUsecThirtyLayer = string.Equals(ent.Layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
                    var isUsecTwentyLayer = string.Equals(ent.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
                    var isUsecBaseLayer = string.Equals(ent.Layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase);
                    if (!isUsecLayer)
                    {
                        isUsecLayer = isUsecZeroLayer || isUsecThirtyLayer;
                    }

                    isUsecLayer = isUsecLayer || isUsecTwentyLayer || isUsecBaseLayer;
                    var isSecLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsecLayer && !isSecLayer)
                    {
                        continue;
                    }

                    if (IsHorizontalLike(a, b))
                    {
                        if (isUsecLayer)
                        {
                            usecHorizontals.Add((id, a, b, generatedSet.Contains(id), isUsecTwentyLayer, isUsecBaseLayer));
                        }
                    }

                    if (IsVerticalLike(a, b))
                    {
                        originalVerticals.Add((id, a, b, isSecLayer, generatedSet.Contains(id), isUsecZeroLayer));
                    }
                }

                if (usecHorizontals.Count == 0 || originalVerticals.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine("Cleanup: SE L-USEC south 20.11 connect skipped (missing horizontals or original vertical boundaries).");
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double minExtend = 0.10;
                const double maxExtend = 40.0;
                const double expectedSouthOffset = CorrectionLinePairGapMeters;
                const double southOffsetTol = 3.0;
                const double southBandMatchTol = 1.5;
                const double originalSeSnapBuffer = 11.0;
                // Allow small drafting noise/rotation drift while still requiring on-segment intersection.
                const double targetOnHorizontalTol = 2.50;
                var usedVerticalBoundaries = new HashSet<ObjectId>();
                var connected = 0;
                var sectionsWithOriginalBoundary = 0;
                var sectionsWithHorizontalCandidate = 0;
                var candidatesEvaluated = 0;
                var blockedByInterveningHorizontalRoad = 0;
                var skippedHorizontalNonSpanning = 0;
                var movedVerticalBoundaryEndpoints = 0;
                var movedHorizontalBoundaryEndpoints = 0;
                var rejectedNonVerticalBoundary = 0;
                var rejectedNonHorizontalBoundary = 0;
                var lsdTargetMidpoints = new List<(ObjectId SectionId, Point2d Midpoint)>();

                for (var si = 0; si < sectionTargets.Count; si++)
                {
                    var sectionTarget = sectionTargets[si];
                    var swCorner = sectionTarget.SwCorner;
                    var eastUnit = sectionTarget.EastUnit;
                    var northUnit = sectionTarget.NorthUnit;

                    double ToU(Point2d p) => (p - swCorner).DotProduct(eastUnit);
                    double ToV(Point2d p) => (p - swCorner).DotProduct(northUnit);
                    bool FirstPointIsSouth(Point2d p0, Point2d p1)
                    {
                        // Prefer world-Y south for endpoint selection; fall back to local-V only when nearly flat in Y.
                        if (Math.Abs(p0.Y - p1.Y) > 1e-3)
                        {
                            return p0.Y <= p1.Y;
                        }

                        return ToV(p0) <= ToV(p1);
                    }

                    var southBandHorizontalSpans = new List<(double MinU, double MaxU)>();
                    for (var hi = 0; hi < usecHorizontals.Count; hi++)
                    {
                        var h = usecHorizontals[hi];
                        if (!DoesSegmentIntersectWindow(h.A, h.B, sectionTarget.Window))
                        {
                            continue;
                        }

                        var hd = h.B - h.A;
                        var hEastComp = Math.Abs(hd.DotProduct(eastUnit));
                        var hNorthComp = Math.Abs(hd.DotProduct(northUnit));
                        if (hEastComp < hNorthComp)
                        {
                            continue;
                        }

                        var hVa = ToV(h.A);
                        var hVb = ToV(h.B);
                        var hVLine = 0.5 * (hVa + hVb);
                        if (Math.Abs(Math.Abs(hVLine) - expectedSouthOffset) > southOffsetTol)
                        {
                            continue;
                        }

                        var hUa = ToU(h.A);
                        var hUb = ToU(h.B);
                        southBandHorizontalSpans.Add((Math.Min(hUa, hUb), Math.Max(hUa, hUb)));
                    }

                    double ClosestSouthBandGap(double boundaryU)
                    {
                        if (southBandHorizontalSpans.Count == 0)
                        {
                            return double.MaxValue;
                        }

                        var best = double.MaxValue;
                        for (var i = 0; i < southBandHorizontalSpans.Count; i++)
                        {
                            var span = southBandHorizontalSpans[i];
                            var gap = 0.0;
                            if (boundaryU < span.MinU)
                            {
                                gap = span.MinU - boundaryU;
                            }
                            else if (boundaryU > span.MaxU)
                            {
                                gap = boundaryU - span.MaxU;
                            }

                            if (gap < best)
                            {
                                best = gap;
                            }
                        }

                        return best;
                    }

                    var eastBoundaryCandidates = new List<(
                        ObjectId Id,
                        Point2d A,
                        Point2d B,
                        double ULine,
                        double ClosestSouthBandGap,
                        bool IsSecLayer,
                        bool IsGenerated,
                        bool IsUsecZeroLayer,
                        bool ContainsSouthBand,
                        double SouthBandDistance,
                        bool NearOriginalSe,
                        double OriginalSeDistance)>();
                    for (var i = 0; i < originalVerticals.Count; i++)
                    {
                        var seg = originalVerticals[i];
                        if (usedVerticalBoundaries.Contains(seg.Id))
                        {
                            continue;
                        }

                        if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                        {
                            continue;
                        }

                        var d = seg.B - seg.A;
                        var eastComp = Math.Abs(d.DotProduct(eastUnit));
                        var northComp = Math.Abs(d.DotProduct(northUnit));
                        if (northComp <= eastComp)
                        {
                            continue;
                        }

                        var uA = ToU(seg.A);
                        var uB = ToU(seg.B);
                        var vA = ToV(seg.A);
                        var vB = ToV(seg.B);
                        var uLine = 0.5 * (uA + uB);
                        var minV = Math.Min(vA, vB);
                        var maxV = Math.Max(vA, vB);
                        var southBandV = -expectedSouthOffset;
                        var containsSouthBand =
                            southBandV >= (minV - southBandMatchTol) &&
                            southBandV <= (maxV + southBandMatchTol);
                        var southBandDistance =
                            southBandV < minV
                                ? (minV - southBandV)
                                : southBandV > maxV
                                    ? (southBandV - maxV)
                                    : 0.0;

                        var southEndpointCandidate = FirstPointIsSouth(seg.A, seg.B) ? seg.A : seg.B;
                        var originalSeDistance = sectionTarget.HasOriginalSeCorner
                            ? southEndpointCandidate.GetDistanceTo(sectionTarget.OriginalSeCorner)
                            : double.MaxValue;
                        var nearOriginalSe = sectionTarget.HasOriginalSeCorner &&
                                             originalSeDistance <= originalSeSnapBuffer;

                        var closestSouthBandGap = ClosestSouthBandGap(uLine);
                        eastBoundaryCandidates.Add((seg.Id, seg.A, seg.B, uLine, closestSouthBandGap, seg.IsSecLayer, seg.IsGenerated, seg.IsUsecZeroLayer, containsSouthBand, southBandDistance, nearOriginalSe, originalSeDistance));
                    }

                    if (eastBoundaryCandidates.Count == 0)
                    {
                        continue;
                    }

                    if (southBandHorizontalSpans.Count == 0)
                    {
                        continue;
                    }

                    var availableBoundaryCandidates = eastBoundaryCandidates;

                    if (sectionTarget.HasOriginalSeCorner)
                    {
                        availableBoundaryCandidates = availableBoundaryCandidates
                            .OrderByDescending(c => c.NearOriginalSe)
                            .ThenBy(c => c.OriginalSeDistance)
                            .ThenBy(c => Math.Abs(c.ULine))
                            .ToList();
                    }

                    const double boundaryToSouthBandMaxGap = 24.0;
                    var nearSouthBandBoundaries = availableBoundaryCandidates
                        .Where(c => c.ClosestSouthBandGap <= boundaryToSouthBandMaxGap)
                        .ToList();
                    if (nearSouthBandBoundaries.Count > 0)
                    {
                        availableBoundaryCandidates = nearSouthBandBoundaries;
                    }

                    var sectionNumber = 0;
                    if (sectionKeyById.TryGetValue(sectionTarget.SectionId, out var sectionKeyForFilter))
                    {
                        sectionNumber = ParseSectionNumber(sectionKeyForFilter.Section);
                    }

                    var preferWestMostSeRaBoundary = IsWestMostSectionForSeRaBoundary(sectionNumber);
                    var trimHorizontalToBoundaryInSpecialSe = preferWestMostSeRaBoundary;

                    var eastRoadAllowanceWestBand = preferWestMostSeRaBoundary ? 75.0 : 45.0;
                    var eastRoadAllowanceEastBand = preferWestMostSeRaBoundary ? 25.0 : 20.0;
                    var eastBandBoundaries = availableBoundaryCandidates
                        .Where(c =>
                            c.ULine >= (sectionTarget.EastEdgeU - eastRoadAllowanceWestBand) &&
                            c.ULine <= (sectionTarget.EastEdgeU + eastRoadAllowanceEastBand))
                        .ToList();
                    if (eastBandBoundaries.Count > 0)
                    {
                        availableBoundaryCandidates = eastBandBoundaries;
                    }

                    if (preferWestMostSeRaBoundary)
                    {
                        var usecZeroBoundaries = availableBoundaryCandidates
                            .Where(c => c.IsUsecZeroLayer)
                            .ToList();
                        if (usecZeroBoundaries.Count > 0)
                        {
                            availableBoundaryCandidates = usecZeroBoundaries;
                        }
                    }

                    var orderedBoundaryCandidates = preferWestMostSeRaBoundary
                        ? availableBoundaryCandidates
                            .OrderByDescending(c => c.IsUsecZeroLayer)
                            .ThenByDescending(c => c.NearOriginalSe)
                            .ThenBy(c => c.OriginalSeDistance)
                            .ThenBy(c => c.ContainsSouthBand ? 0 : 1)
                            .ThenBy(c => c.SouthBandDistance)
                            .ThenBy(c => c.ClosestSouthBandGap)
                            .ThenBy(c => c.ULine)
                            .ThenBy(c => c.IsGenerated ? 1 : 0)
                            .ThenByDescending(c => c.IsSecLayer)
                            .ToList()
                        : availableBoundaryCandidates
                        .OrderBy(c => c.ContainsSouthBand ? 0 : 1)
                        .ThenBy(c => c.SouthBandDistance)
                        .ThenBy(c => c.ClosestSouthBandGap)
                        .ThenBy(c => c.OriginalSeDistance)
                        .ThenByDescending(c => c.IsSecLayer)
                        .ThenBy(c => c.ULine)
                        .ToList();
                    if (orderedBoundaryCandidates.Count == 0)
                    {
                        continue;
                    }

                    // SE bridge must only move the 0-side vertical boundary.
                    // Moving SEC/USEC/2012/3018 boundaries causes cross-township inconsistency.
                    var usecZeroBoundaryCandidates = orderedBoundaryCandidates
                        .Where(c => c.IsUsecZeroLayer)
                        .ToList();
                    if (usecZeroBoundaryCandidates.Count == 0)
                    {
                        continue;
                    }

                    orderedBoundaryCandidates = usecZeroBoundaryCandidates;

                    sectionsWithOriginalBoundary++;

                    var horizontalCandidates = new List<(
                        ObjectId SegmentId,
                        Point2d SegmentA,
                        Point2d SegmentB,
                        Point2d TargetPoint,
                        Point2d HorizontalMidpoint,
                        double Score,
                        bool IsUsecTwentyLayer,
                        bool IsUsecBaseLayer)>();
                    var selectedBoundary = orderedBoundaryCandidates[0];
                    var selectedBoundaryFound = false;

                    for (var bi = 0; bi < orderedBoundaryCandidates.Count; bi++)
                    {
                        var boundaryCandidate = orderedBoundaryCandidates[bi];
                        var boundaryVa = ToV(boundaryCandidate.A);
                        var boundaryVb = ToV(boundaryCandidate.B);
                        var southIsStart = FirstPointIsSouth(boundaryCandidate.A, boundaryCandidate.B);
                        var southPoint = southIsStart ? boundaryCandidate.A : boundaryCandidate.B;
                        var northPoint = southIsStart ? boundaryCandidate.B : boundaryCandidate.A;
                        var boundaryDir = northPoint - southPoint;
                        var boundaryLen = boundaryDir.Length;
                        if (boundaryLen <= 1e-6)
                        {
                            continue;
                        }

                        boundaryDir = boundaryDir / boundaryLen;
                        var boundaryU = boundaryCandidate.ULine;

                        var candidatesForBoundary = new List<(
                            ObjectId SegmentId,
                            Point2d SegmentA,
                            Point2d SegmentB,
                            Point2d TargetPoint,
                            Point2d HorizontalMidpoint,
                            double Score,
                            bool IsUsecTwentyLayer,
                            bool IsUsecBaseLayer)>();
                        for (var i = 0; i < usecHorizontals.Count; i++)
                        {
                            var seg = usecHorizontals[i];
                            if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                            {
                                continue;
                            }

                            var d = seg.B - seg.A;
                            var eastComp = Math.Abs(d.DotProduct(eastUnit));
                            var northComp = Math.Abs(d.DotProduct(northUnit));
                            if (eastComp < northComp)
                            {
                                continue;
                            }

                            var uA = ToU(seg.A);
                            var uB = ToU(seg.B);
                            var minU = Math.Min(uA, uB);
                            var maxU = Math.Max(uA, uB);
                            var vA = ToV(seg.A);
                            var vB = ToV(seg.B);
                            var vLine = 0.5 * (vA + vB);
                            if (Math.Abs(Math.Abs(vLine) - expectedSouthOffset) > southOffsetTol)
                            {
                                continue;
                            }

                            var spansBoundary = boundaryU >= (minU - 1.0) && boundaryU <= (maxU + 1.0);

                            var boundaryGap = 0.0;
                            if (boundaryU < minU)
                            {
                                boundaryGap = minU - boundaryU;
                            }
                            else if (boundaryU > maxU)
                            {
                                boundaryGap = boundaryU - maxU;
                            }

                            var apparentBoundarySpanTol = preferWestMostSeRaBoundary ? 45.0 : 22.0;
                            if (!spansBoundary && boundaryGap > apparentBoundarySpanTol)
                            {
                                skippedHorizontalNonSpanning++;
                                continue;
                            }

                            if (!TryIntersectInfiniteLines(seg.A, seg.B, boundaryCandidate.A, boundaryCandidate.B, out var targetPoint))
                            {
                                continue;
                            }

                            if (!IsPointInWindow(targetPoint, sectionTarget.Window))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(targetPoint, seg.A, seg.B) > targetOnHorizontalTol)
                            {
                                skippedHorizontalNonSpanning++;
                                continue;
                            }

                            var tOnBoundary = (targetPoint - southPoint).DotProduct(boundaryDir);
                            var boundaryEndpointGap = 0.0;
                            if (tOnBoundary < -minExtend)
                            {
                                boundaryEndpointGap = -tOnBoundary;
                            }
                            else if (tOnBoundary > (boundaryLen + minExtend))
                            {
                                boundaryEndpointGap = tOnBoundary - (boundaryLen + minExtend);
                            }

                            var boundaryEndpointGapMax = preferWestMostSeRaBoundary ? 45.0 : 22.0;
                            if (boundaryEndpointGap > boundaryEndpointGapMax)
                            {
                                continue;
                            }

                            var horizontalMoveDist = Math.Min(
                                seg.A.GetDistanceTo(targetPoint),
                                seg.B.GetDistanceTo(targetPoint));
                            if (horizontalMoveDist <= endpointMoveTol || horizontalMoveDist > maxExtend)
                            {
                                continue;
                            }

                            var score = boundaryEndpointGap;
                            score += 0.20 * boundaryGap;
                            score += 0.10 * horizontalMoveDist;
                            if (!seg.IsGenerated)
                            {
                                score += 0.25;
                            }
                            if (sectionTarget.HasOriginalSeCorner)
                            {
                                score += 0.25 * DistancePointToSegment(sectionTarget.OriginalSeCorner, seg.A, seg.B);
                            }

                            candidatesForBoundary.Add((
                                seg.Id,
                                seg.A,
                                seg.B,
                                targetPoint,
                                Midpoint(seg.A, seg.B),
                                score,
                                seg.IsUsecTwentyLayer,
                                seg.IsUsecBaseLayer));
                        }

                        if (candidatesForBoundary.Count == 0)
                        {
                            continue;
                        }

                        selectedBoundary = boundaryCandidate;
                        selectedBoundaryFound = true;
                        horizontalCandidates = candidatesForBoundary;
                        break;
                    }

                    if (!selectedBoundaryFound)
                    {
                        continue;
                    }

                    candidatesEvaluated += horizontalCandidates.Count;
                    if (horizontalCandidates.Count == 0)
                    {
                        continue;
                    }

                    sectionsWithHorizontalCandidate++;
                    var orderedCandidates = horizontalCandidates
                        .OrderBy(c => c.Score)
                        .ToList();
                    var hasUsecTwentyCandidate = orderedCandidates.Any(c => c.IsUsecTwentyLayer);
                    var bestScore = orderedCandidates[0].Score;
                    var connectedThisSection = 0;
                    var maxConnectionsPerSection = trimHorizontalToBoundaryInSpecialSe ? 1 : 2;
                    const double additionalScoreTolerance = 8.0;
                    for (var ci = 0; ci < orderedCandidates.Count && connectedThisSection < maxConnectionsPerSection; ci++)
                    {
                        var best = orderedCandidates[ci];
                        if (best.Score > bestScore + additionalScoreTolerance)
                        {
                            break;
                        }

                        if (trimHorizontalToBoundaryInSpecialSe)
                        {
                            if (!best.IsUsecTwentyLayer)
                            {
                                if (!(best.IsUsecBaseLayer && !hasUsecTwentyCandidate))
                                {
                                    continue;
                                }
                            }

                            var bestVLine = 0.5 * (ToV(best.SegmentA) + ToV(best.SegmentB));
                            if (Math.Abs(Math.Abs(bestVLine) - expectedSouthOffset) > southBandMatchTol)
                            {
                                continue;
                            }

                            if (!(tr.GetObject(best.SegmentId, OpenMode.ForRead, false) is Entity horizontalEntity) ||
                                horizontalEntity.IsErased)
                            {
                                continue;
                            }

                            if (!TryReadOpenSegment(horizontalEntity, out var h0, out var h1) || !IsHorizontalLike(h0, h1))
                            {
                                rejectedNonHorizontalBoundary++;
                                continue;
                            }

                            bool TrySnapUsecZeroBoundaryToHorizontal(Point2d horizontalA, Point2d horizontalB)
                            {
                                var usecZeroBoundaryCandidates = eastBoundaryCandidates
                                    .Where(c => c.IsUsecZeroLayer)
                                    .OrderBy(c => usedVerticalBoundaries.Contains(c.Id) ? 1 : 0)
                                    .ThenByDescending(c => c.NearOriginalSe)
                                    .ThenBy(c => c.OriginalSeDistance)
                                    .ThenBy(c => c.SouthBandDistance)
                                    .ThenBy(c => Math.Abs(c.ULine - sectionTarget.EastEdgeU))
                                    .ToList();
                                for (var zi = 0; zi < usecZeroBoundaryCandidates.Count; zi++)
                                {
                                    var zeroBoundary = usecZeroBoundaryCandidates[zi];
                                    if (!(tr.GetObject(zeroBoundary.Id, OpenMode.ForWrite, false) is Entity writableBoundaryInSpecial) ||
                                        writableBoundaryInSpecial.IsErased)
                                    {
                                        continue;
                                    }

                                    if (!TryReadOpenSegment(writableBoundaryInSpecial, out var b0Special, out var b1Special) ||
                                        !IsVerticalLike(b0Special, b1Special))
                                    {
                                        continue;
                                    }

                                    if (!TryIntersectInfiniteLines(horizontalA, horizontalB, b0Special, b1Special, out var zeroTarget))
                                    {
                                        continue;
                                    }

                                    if (!IsPointInWindow(zeroTarget, sectionTarget.Window))
                                    {
                                        continue;
                                    }

                                    if (DistancePointToSegment(zeroTarget, horizontalA, horizontalB) > targetOnHorizontalTol)
                                    {
                                        continue;
                                    }

                                    var moveSouthStart = FirstPointIsSouth(b0Special, b1Special);
                                    var southEndpointSpecial = moveSouthStart ? b0Special : b1Special;
                                    var endpointMoveSpecial = southEndpointSpecial.GetDistanceTo(zeroTarget);
                                    if (endpointMoveSpecial <= endpointMoveTol || endpointMoveSpecial > maxExtend)
                                    {
                                        continue;
                                    }

                                    if (TryMoveEndpoint(writableBoundaryInSpecial, moveSouthStart, zeroTarget, endpointMoveTol))
                                    {
                                        movedVerticalBoundaryEndpoints++;
                                        usedVerticalBoundaries.Add(zeroBoundary.Id);
                                        return true;
                                    }
                                }

                                return false;
                            }

                            if (TrySnapUsecZeroBoundaryToHorizontal(h0, h1))
                            {
                                connected++;
                                connectedThisSection++;
                            }

                            continue;
                        }

                        // Directional fix for SE quarter bridge:
                        // move the N-S boundary endpoint (south end) to the apparent intersection,
                        // instead of extending the E-W horizontal segment.
                        if (!(tr.GetObject(selectedBoundary.Id, OpenMode.ForWrite, false) is Entity writableBoundary) || writableBoundary.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableBoundary, out var b0, out var b1) || !IsVerticalLike(b0, b1))
                        {
                            rejectedNonVerticalBoundary++;
                            continue;
                        }

                        var moveStart = FirstPointIsSouth(b0, b1); // move south endpoint only
                        var southEndpoint = moveStart ? b0 : b1;
                        var endpointMove = southEndpoint.GetDistanceTo(best.TargetPoint);
                        if (endpointMove <= endpointMoveTol || endpointMove > maxExtend)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writableBoundary, moveStart, best.TargetPoint, endpointMoveTol))
                        {
                            continue;
                        }

                        // Keep SE pass from pulling LSD endpoints until SE geometry is deterministic.
                        // (Historical source of bottom-township cross-through artifacts.)
                        // lsdTargetMidpoints.Add((sectionTarget.SectionId, Midpoint(westPoint, best.TargetPoint)));

                        connected++;
                        connectedThisSection++;
                        movedVerticalBoundaryEndpoints++;
                        usedVerticalBoundaries.Add(selectedBoundary.Id);
                    }
                }

                var lsdAdjusted = 0;
                if (lsdTargetMidpoints.Count > 0)
                {
                    var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                        {
                            continue;
                        }

                        if (!string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(ent, out var a, out var b))
                        {
                            continue;
                        }

                        if (!DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(a, b))
                        {
                            continue;
                        }

                        lsdSegments.Add((id, a, b));
                    }

                    const double lsdMaxMove = 80.0;
                    const double southwardTol = 0.50;
                    const double maxSouthwardDelta = 70.0;
                    const double maxCenterlineOffset = 35.0;

                    bool TryGetOwningSectionIndex(Point2d a, Point2d b, out int sectionIndex)
                    {
                        const double ownershipUTol = 40.0;
                        sectionIndex = -1;
                        var mid = Midpoint(a, b);
                        var bestDistance = double.MaxValue;
                        for (var si = 0; si < sectionTargets.Count; si++)
                        {
                            var sectionTarget = sectionTargets[si];
                            if (!DoesSegmentIntersectWindow(a, b, sectionTarget.Window) &&
                                !IsPointInWindow(mid, sectionTarget.Window))
                            {
                                continue;
                            }

                            var midU = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            if (midU < -ownershipUTol || midU > (sectionTarget.EastEdgeU + ownershipUTol))
                            {
                                continue;
                            }

                            var d = mid.GetDistanceTo(sectionTarget.SwCorner);
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                sectionIndex = si;
                            }
                        }

                        return sectionIndex >= 0;
                    }

                    bool TrySelectSouthTargetMidpoint(
                        Point2d southEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint)
                    {
                        targetMidpoint = southEndpoint;
                        var sectionTarget = sectionTargets[sectionIndex];
                        var endpointU = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                        var endpointV = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);

                        var found = false;
                        var bestCenterlineOffset = double.MaxValue;
                        var bestSouthDelta = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < lsdTargetMidpoints.Count; i++)
                        {
                            var adj = lsdTargetMidpoints[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var targetU = (adj.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > maxCenterlineOffset)
                            {
                                continue;
                            }

                            var targetV = (adj.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var southDelta = endpointV - targetV;
                            if (southDelta < -southwardTol || southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            var move = southEndpoint.GetDistanceTo(adj.Midpoint);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterCenterline = centerlineOffset < (bestCenterlineOffset - 1e-6);
                            var tiedCenterline = Math.Abs(centerlineOffset - bestCenterlineOffset) <= 1e-6;
                            var betterSouth = tiedCenterline && southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = tiedCenterline && Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterCenterline && !betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestCenterlineOffset = centerlineOffset;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.Midpoint;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Strict: do not use neighboring-section nearest fallback.
                        return false;
                    }

                    for (var i = 0; i < lsdSegments.Count; i++)
                    {
                        var lsd = lsdSegments[i];
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsVerticalLike(p0, p1))
                        {
                            continue;
                        }

                        if (!TryGetOwningSectionIndex(p0, p1, out var sectionIndex))
                        {
                            continue;
                        }

                        var sectionTarget = sectionTargets[sectionIndex];
                        var v0 = (p0 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                        var v1 = (p1 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                        var moveStart = v0 <= v1;
                        var southEndpoint = moveStart ? p0 : p1;
                        if (!TrySelectSouthTargetMidpoint(southEndpoint, sectionIndex, out var targetMid))
                        {
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices >= 2)
                        {
                            var index = moveStart ? 0 : lsdPoly.NumberOfVertices - 1;
                            lsdPoly.SetPointAt(index, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                tr.Commit();
                if (connected > 0)
                {
                    logger?.WriteLine($"Cleanup: connected {connected} SE L-USEC south 20.11 line(s) to west-most east RA original boundary.");
                    logger?.WriteLine($"Cleanup: SE directional bridge moves verticalEndpoints={movedVerticalBoundaryEndpoints}, horizontalEndpoints={movedHorizontalBoundaryEndpoints}, rejectedNonVerticalBoundary={rejectedNonVerticalBoundary}, rejectedNonHorizontalBoundary={rejectedNonHorizontalBoundary}.");
                    if (blockedByInterveningHorizontalRoad > 0)
                    {
                        logger?.WriteLine($"Cleanup: SE guard blocked {blockedByInterveningHorizontalRoad} candidate endpoint move(s) due to intervening horizontal road boundaries.");
                    }
                    if (skippedHorizontalNonSpanning > 0)
                    {
                        logger?.WriteLine($"Cleanup: SE guard skipped {skippedHorizontalNonSpanning} horizontal candidate(s) that did not span selected east boundary.");
                    }
                    if (lsdAdjusted > 0)
                    {
                        logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of SE L-USEC south 20.11 connection(s).");
                    }
                }
                else
                {
                    logger?.WriteLine($"Cleanup: SE directional bridge moves verticalEndpoints={movedVerticalBoundaryEndpoints}, horizontalEndpoints={movedHorizontalBoundaryEndpoints}, rejectedNonVerticalBoundary={rejectedNonVerticalBoundary}, rejectedNonHorizontalBoundary={rejectedNonHorizontalBoundary}.");
                    if (blockedByInterveningHorizontalRoad > 0)
                    {
                        logger?.WriteLine($"Cleanup: SE guard blocked {blockedByInterveningHorizontalRoad} candidate endpoint move(s) due to intervening horizontal road boundaries.");
                    }
                    if (skippedHorizontalNonSpanning > 0)
                    {
                        logger?.WriteLine($"Cleanup: SE guard skipped {skippedHorizontalNonSpanning} horizontal candidate(s) that did not span selected east boundary.");
                    }
                    logger?.WriteLine(
                        $"Cleanup: SE L-USEC south 20.11 connect found no candidates " +
                        $"(sections={sectionTargets.Count}, withBoundary={sectionsWithOriginalBoundary}, withH={sectionsWithHorizontalCandidate}, candidates={candidatesEvaluated}).");
                }
            }

            // SE specific duplicate cleanup: equal-length blind-line twins can remain after SE connect.
            CleanupDuplicateBlindLineSegments(database, targetSectionIds, logger);
        }

        private static bool IsWestMostSectionForSeRaBoundary(int sectionNumber)
        {
            return (sectionNumber >= 1 && sectionNumber <= 6) ||
                   (sectionNumber >= 13 && sectionNumber <= 18) ||
                   (sectionNumber >= 25 && sectionNumber <= 30);
        }
    }
}
