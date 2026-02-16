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
        private static void EnforceSecLineEndpointsOnHardSectionBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
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

            bool IsSecSourceLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsHardBoundaryLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var hardBoundarySegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var secSourceIds = new List<ObjectId>();
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

                    var layer = ent.Layer ?? string.Empty;
                    if (IsHardBoundaryLayer(layer))
                    {
                        hardBoundarySegments.Add((id, a, b));
                    }

                    if (IsSecSourceLayer(layer))
                    {
                        secSourceIds.Add(id);
                    }
                }

                if (secSourceIds.Count == 0 || hardBoundarySegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTouchTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double minExtend = 0.05;
                const double maxExtend = 40.0;
                const double minRemainingLength = 2.0;
                const double endpointAxisTol = 0.80;
                const double outerBoundaryTol = 0.40;

                var scannedEndpoints = 0;
                var alreadyOnHard = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;
                var fallbackEndpointUsed = 0;

                bool IsEndpointOnHardBoundary(Point2d endpoint, ObjectId sourceId)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindExtensionTarget(Point2d endpoint, Point2d other, ObjectId sourceId, out Point2d target)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var usedFallback = false;
                    var bestT = double.MaxValue;
                    void ConsiderCandidate(double t, bool isFallback)
                    {
                        if (t <= minExtend || t > maxExtend)
                        {
                            return;
                        }

                        var projectedFromOther = ((endpoint + (outwardDir * t)) - other).DotProduct(outwardDir);
                        if (projectedFromOther < minRemainingLength)
                        {
                            return;
                        }

                        var better =
                            !found ||
                            t < (bestT - 1e-6) ||
                            (Math.Abs(t - bestT) <= 1e-6 && usedFallback && !isFallback);
                        if (!better)
                        {
                            return;
                        }

                        found = true;
                        usedFallback = isFallback;
                        bestT = t;
                    }

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId)
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t))
                        {
                            continue;
                        }

                        ConsiderCandidate(t, isFallback: false);
                    }

                    // Include near-collinear endpoint candidates so "apparent intersection"
                    // on a boundary endpoint can win over a farther strict crossing.
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId)
                        {
                            continue;
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var candidate = endpointIndex == 0 ? seg.A : seg.B;
                            if (DistancePointToInfiniteLine(candidate, endpoint, endpoint + outwardDir) > endpointAxisTol)
                            {
                                continue;
                            }

                            var t = (candidate - endpoint).DotProduct(outwardDir);
                            ConsiderCandidate(t, isFallback: true);
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    if (usedFallback)
                    {
                        fallbackEndpointUsed++;
                    }

                    target = endpoint + (outwardDir * bestT);
                    return true;
                }

                for (var i = 0; i < secSourceIds.Count; i++)
                {
                    var sourceId = secSourceIds[i];
                    if (!(tr.GetObject(sourceId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;

                    scannedEndpoints++;
                    if (IsEndpointOnHardBoundary(p0, sourceId))
                    {
                        alreadyOnHard++;
                    }
                    else if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else if (TryFindExtensionTarget(p0, p1, sourceId, out var snappedStart))
                    {
                        moveStart = true;
                        targetStart = snappedStart;
                    }
                    else
                    {
                        noTarget++;
                    }

                    scannedEndpoints++;
                    if (IsEndpointOnHardBoundary(p1, sourceId))
                    {
                        alreadyOnHard++;
                    }
                    else if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else if (TryFindExtensionTarget(p1, p0, sourceId, out var snappedEnd))
                    {
                        moveEnd = true;
                        targetEnd = snappedEnd;
                    }
                    else
                    {
                        noTarget++;
                    }

                    if (moveStart && moveEnd && targetStart.GetDistanceTo(targetEnd) < minRemainingLength)
                    {
                        var startMoveDist = p0.GetDistanceTo(targetStart);
                        var endMoveDist = p1.GetDistanceTo(targetEnd);
                        if (startMoveDist >= endMoveDist)
                        {
                            moveEnd = false;
                        }
                        else
                        {
                            moveStart = false;
                        }
                    }

                    if (!moveStart && !moveEnd)
                    {
                        continue;
                    }

                    var movedLine = false;
                    if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (movedLine)
                    {
                        adjustedLines++;
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: L-SEC endpoint-on-hard rule scannedEndpoints={scannedEndpoints}, alreadyOnHard={alreadyOnHard}, windowBoundarySkipped={boundarySkipped}, noTarget={noTarget}, fallbackEndpointUsed={fallbackEndpointUsed}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
            }
        }

        private static void EnforceQuarterLineEndpointsOnSectionBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
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

            bool IsQuarterLineLayer(string layer)
            {
                return string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsSectionBoundaryLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var boundarySegments = new List<(Point2d A, Point2d B)>();
                var qsecLineIds = new List<ObjectId>();
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

                    var layer = ent.Layer ?? string.Empty;
                    if (IsSectionBoundaryLayer(layer))
                    {
                        boundarySegments.Add((a, b));
                        continue;
                    }

                    if (IsQuarterLineLayer(layer))
                    {
                        qsecLineIds.Add(id);
                    }
                }

                if (boundarySegments.Count == 0 || qsecLineIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTouchTol = 0.30;
                const double endpointMoveTol = 0.05;
                const double minMove = 0.05;
                const double maxMove = 40.0;
                const double minRemainingLength = 2.0;
                const double endpointAxisTol = 0.80;
                const double outerBoundaryTol = 0.40;
                var scannedEndpoints = 0;
                var alreadyOnBoundary = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;

                bool IsEndpointOnValidBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < boundarySegments.Count; i++)
                    {
                        var seg = boundarySegments[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindSnapTarget(Point2d endpoint, Point2d other, out Point2d target)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var bestAbsT = double.MaxValue;
                    var bestT = 0.0;
                    var bestIsFallback = true;
                    void ConsiderCandidate(double t, bool isFallback)
                    {
                        var absT = Math.Abs(t);
                        if (absT <= minMove || absT > maxMove)
                        {
                            return;
                        }

                        // Do not allow a target that would invert/collapse the line through its opposite endpoint.
                        if (t < 0.0 && (outwardLen + t) < minRemainingLength)
                        {
                            return;
                        }

                        var isBetter =
                            !found ||
                            absT < (bestAbsT - 1e-6) ||
                            (Math.Abs(absT - bestAbsT) <= 1e-6 &&
                                (bestIsFallback && !isFallback || t < bestT));
                        if (!isBetter)
                        {
                            return;
                        }

                        found = true;
                        bestAbsT = absT;
                        bestT = t;
                        bestIsFallback = isFallback;
                    }

                    for (var i = 0; i < boundarySegments.Count; i++)
                    {
                        var seg = boundarySegments[i];
                        if (!TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t))
                        {
                            continue;
                        }

                        ConsiderCandidate(t, isFallback: false);
                    }

                    for (var i = 0; i < boundarySegments.Count; i++)
                    {
                        var seg = boundarySegments[i];
                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var candidate = endpointIndex == 0 ? seg.A : seg.B;
                            if (DistancePointToInfiniteLine(candidate, endpoint, endpoint + outwardDir) > endpointAxisTol)
                            {
                                continue;
                            }

                            var t = (candidate - endpoint).DotProduct(outwardDir);
                            ConsiderCandidate(t, isFallback: true);
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = endpoint + (outwardDir * bestT);
                    return true;
                }

                for (var i = 0; i < qsecLineIds.Count; i++)
                {
                    var id = qsecLineIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;

                    scannedEndpoints++;
                    if (IsEndpointOnValidBoundary(p0))
                    {
                        alreadyOnBoundary++;
                    }
                    else if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else if (TryFindSnapTarget(p0, p1, out var snappedStart))
                    {
                        moveStart = true;
                        targetStart = snappedStart;
                    }
                    else
                    {
                        noTarget++;
                    }

                    scannedEndpoints++;
                    if (IsEndpointOnValidBoundary(p1))
                    {
                        alreadyOnBoundary++;
                    }
                    else if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else if (TryFindSnapTarget(p1, p0, out var snappedEnd))
                    {
                        moveEnd = true;
                        targetEnd = snappedEnd;
                    }
                    else
                    {
                        noTarget++;
                    }

                    if (!moveStart && !moveEnd)
                    {
                        continue;
                    }

                    var movedLine = false;
                    if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (movedLine)
                    {
                        adjustedLines++;
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: 1/4 endpoint-on-section rule scanned={scannedEndpoints}, alreadyOnBoundary={alreadyOnBoundary}, windowBoundarySkipped={boundarySkipped}, noTarget={noTarget}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
            }
        }

        private static void EnforceLsdLineEndpointsOnHardSectionBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
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

            bool IsUsecZeroBoundaryLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
            }

            bool IsUsecTwentyBoundaryLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsThirtyEighteenLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var hardBoundarySegments = new List<(Point2d A, Point2d B, bool IsZero)>();
                var thirtyBoundarySegments = new List<(Point2d A, Point2d B)>();
                var horizontalMidpointTargetSegments = new List<(Point2d A, Point2d B, Point2d Mid, int Priority)>();
                var verticalMidpointTargetSegments = new List<(Point2d A, Point2d B, Point2d Mid, int Priority)>();
                var qsecHorizontalComponentTargets = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var qsecVerticalComponentTargets = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var qsecHorizontalSegments = new List<(Point2d A, Point2d B)>();
                var qsecVerticalSegments = new List<(Point2d A, Point2d B)>();
                var lsdLineIds = new List<ObjectId>();
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

                    var layer = ent.Layer ?? string.Empty;
                    if (IsUsecZeroBoundaryLayer(layer))
                    {
                        hardBoundarySegments.Add((a, b, IsZero: true));
                        if (IsHorizontalLike(a, b))
                        {
                            horizontalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 2));
                        }
                        else if (IsVerticalLike(a, b))
                        {
                            verticalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 2));
                        }

                        continue;
                    }

                    if (IsUsecTwentyBoundaryLayer(layer))
                    {
                        hardBoundarySegments.Add((a, b, IsZero: false));
                        if (IsHorizontalLike(a, b))
                        {
                            horizontalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 1));
                        }
                        else if (IsVerticalLike(a, b))
                        {
                            verticalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 1));
                        }

                        continue;
                    }

                    if (IsThirtyEighteenLayer(layer))
                    {
                        thirtyBoundarySegments.Add((a, b));
                        continue;
                    }

                    // Midpoint targets for LSD endpoints:
                    // 1) quarter lines (preferred), 2) blind lines, 3) section lines.
                    if (IsHorizontalLike(a, b))
                    {
                        if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                        {
                            qsecHorizontalSegments.Add((a, b));
                        }
                        else if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                        {
                            horizontalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 1));
                        }
                        else if (string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                        {
                            horizontalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 2));
                        }
                    }

                    if (IsVerticalLike(a, b))
                    {
                        if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                        {
                            qsecVerticalSegments.Add((a, b));
                        }
                        else if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                        {
                            verticalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 1));
                        }
                        else if (string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                        {
                            verticalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 2));
                        }
                    }

                    if (string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) &&
                        IsAdjustableLsdLineSegment(a, b))
                    {
                        lsdLineIds.Add(id);
                    }
                }

                // Build midpoint targets for 1/4 lines strictly from final geometry.
                // Midpoints must be computed after all section/endpoint adjustments, with no pre-extension bias.

                for (var i = 0; i < qsecHorizontalSegments.Count; i++)
                {
                    var seg = qsecHorizontalSegments[i];
                    horizontalMidpointTargetSegments.Add((seg.A, seg.B, Midpoint(seg.A, seg.B), Priority: 0));
                }

                for (var i = 0; i < qsecVerticalSegments.Count; i++)
                {
                    var seg = qsecVerticalSegments[i];
                    verticalMidpointTargetSegments.Add((seg.A, seg.B, Midpoint(seg.A, seg.B), Priority: 0));
                }

                // Build shared component midpoint targets for 1/4 lines so sibling LSDs
                // on the same quarter line resolve to an identical midpoint anchor.
                if (qsecHorizontalSegments.Count > 0)
                {
                    const double qsecAxisClusterTol = 2.50;
                    // Bridge one-road-allowance breaks (~10.06m) so sibling LSD endpoints
                    // on the same logical 1/4 line resolve to one shared midpoint.
                    const double qsecMergeGapTol = 14.00;
                    var components = new List<(double MinX, double MaxX, double SumY, int Count)>();
                    for (var i = 0; i < qsecHorizontalSegments.Count; i++)
                    {
                        var seg = qsecHorizontalSegments[i];
                        var y = 0.5 * (seg.A.Y + seg.B.Y);
                        var minX = Math.Min(seg.A.X, seg.B.X);
                        var maxX = Math.Max(seg.A.X, seg.B.X);
                        var merged = false;
                        for (var ci = 0; ci < components.Count; ci++)
                        {
                            var c = components[ci];
                            var axis = c.SumY / Math.Max(1, c.Count);
                            if (Math.Abs(y - axis) > qsecAxisClusterTol)
                            {
                                continue;
                            }

                            if (maxX < (c.MinX - qsecMergeGapTol) || minX > (c.MaxX + qsecMergeGapTol))
                            {
                                continue;
                            }

                            c.MinX = Math.Min(c.MinX, minX);
                            c.MaxX = Math.Max(c.MaxX, maxX);
                            c.SumY += y;
                            c.Count += 1;
                            components[ci] = c;
                            merged = true;
                            break;
                        }

                        if (!merged)
                        {
                            components.Add((minX, maxX, y, 1));
                        }
                    }

                    for (var ci = 0; ci < components.Count; ci++)
                    {
                        var c = components[ci];
                        var y = c.SumY / Math.Max(1, c.Count);
                        var a = new Point2d(c.MinX, y);
                        var b = new Point2d(c.MaxX, y);
                        var mid = Midpoint(a, b);
                        // Component midpoint is fallback only; exact segment midpoint stays authoritative.
                        horizontalMidpointTargetSegments.Add((a, b, mid, Priority: 3));
                        qsecHorizontalComponentTargets.Add((a, b, mid));
                    }
                }

                if (qsecVerticalSegments.Count > 0)
                {
                    const double qsecAxisClusterTol = 2.50;
                    // Bridge one-road-allowance breaks (~10.06m) so sibling LSD endpoints
                    // on the same logical 1/4 line resolve to one shared midpoint.
                    const double qsecMergeGapTol = 14.00;
                    var components = new List<(double MinY, double MaxY, double SumX, int Count)>();
                    for (var i = 0; i < qsecVerticalSegments.Count; i++)
                    {
                        var seg = qsecVerticalSegments[i];
                        var x = 0.5 * (seg.A.X + seg.B.X);
                        var minY = Math.Min(seg.A.Y, seg.B.Y);
                        var maxY = Math.Max(seg.A.Y, seg.B.Y);
                        var merged = false;
                        for (var ci = 0; ci < components.Count; ci++)
                        {
                            var c = components[ci];
                            var axis = c.SumX / Math.Max(1, c.Count);
                            if (Math.Abs(x - axis) > qsecAxisClusterTol)
                            {
                                continue;
                            }

                            if (maxY < (c.MinY - qsecMergeGapTol) || minY > (c.MaxY + qsecMergeGapTol))
                            {
                                continue;
                            }

                            c.MinY = Math.Min(c.MinY, minY);
                            c.MaxY = Math.Max(c.MaxY, maxY);
                            c.SumX += x;
                            c.Count += 1;
                            components[ci] = c;
                            merged = true;
                            break;
                        }

                        if (!merged)
                        {
                            components.Add((minY, maxY, x, 1));
                        }
                    }

                    for (var ci = 0; ci < components.Count; ci++)
                    {
                        var c = components[ci];
                        var x = c.SumX / Math.Max(1, c.Count);
                        var a = new Point2d(x, c.MinY);
                        var b = new Point2d(x, c.MaxY);
                        var mid = Midpoint(a, b);
                        // Component midpoint is fallback only; exact segment midpoint stays authoritative.
                        verticalMidpointTargetSegments.Add((a, b, mid, Priority: 3));
                        qsecVerticalComponentTargets.Add((a, b, mid));
                    }
                }

                // Derive section-center anchors from final QSEC components.
                // Each center is an intersection of horizontal/vertical 1/4 components.
                var qsecCenters = new List<Point2d>();
                if (qsecHorizontalComponentTargets.Count > 0 && qsecVerticalComponentTargets.Count > 0)
                {
                    const double centerOnComponentTol = 2.50;
                    const double centerMergeTol = 1.00;
                    for (var hi = 0; hi < qsecHorizontalComponentTargets.Count; hi++)
                    {
                        var h = qsecHorizontalComponentTargets[hi];
                        var hy = 0.5 * (h.A.Y + h.B.Y);
                        var hMinX = Math.Min(h.A.X, h.B.X);
                        var hMaxX = Math.Max(h.A.X, h.B.X);
                        for (var vi = 0; vi < qsecVerticalComponentTargets.Count; vi++)
                        {
                            var v = qsecVerticalComponentTargets[vi];
                            var vx = 0.5 * (v.A.X + v.B.X);
                            var vMinY = Math.Min(v.A.Y, v.B.Y);
                            var vMaxY = Math.Max(v.A.Y, v.B.Y);
                            if (vx < (hMinX - centerOnComponentTol) || vx > (hMaxX + centerOnComponentTol))
                            {
                                continue;
                            }

                            if (hy < (vMinY - centerOnComponentTol) || hy > (vMaxY + centerOnComponentTol))
                            {
                                continue;
                            }

                            var c = new Point2d(vx, hy);
                            var merged = false;
                            for (var ci = 0; ci < qsecCenters.Count; ci++)
                            {
                                if (qsecCenters[ci].GetDistanceTo(c) <= centerMergeTol)
                                {
                                    merged = true;
                                    break;
                                }
                            }

                            if (!merged)
                            {
                                qsecCenters.Add(c);
                            }
                        }
                    }
                }

                if (hardBoundarySegments.Count == 0 || lsdLineIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTouchTol = 0.30;
                const double endpointMoveTol = 0.005;
                const double midpointEndpointMoveTol = 0.005;
                const double minMove = 0.005;
                const double maxMove = 40.0;
                const double minRemainingLength = 2.0;
                const double outerBoundaryTol = 0.40;
                const double midpointAxisTol = 12.0;

                var scannedEndpoints = 0;
                var alreadyOnHardBoundary = 0;
                var onThirtyOnly = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;
                var midpointAdjustedLines = 0;
                var midpointAdjustedEndpoints = 0;
                var qsecComponentClampLines = 0;
                var qsecComponentClampEndpoints = 0;

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

                bool IsEndpointOnHardBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsEndpointOnUsecZeroBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!seg.IsZero)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsEndpointOnUsecTwentyBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.IsZero)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsEndpointOnThirtyBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < thirtyBoundarySegments.Count; i++)
                    {
                        var seg = thirtyBoundarySegments[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindEndpointHorizontalMidpointX(Point2d endpoint, out double targetX, out double targetY, out int targetPriority)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    targetPriority = int.MaxValue;
                    const double endpointYLineTol = 2.00;
                    const double xSpanTol = 2.00;
                    const double endpointOnSegmentTol = 0.75;
                    const double maxMidpointShift = 40.0;

                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestYGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < horizontalMidpointTargetSegments.Count; i++)
                    {
                        var seg = horizontalMidpointTargetSegments[i];
                        var onSegmentTol = seg.Priority == 0 ? 2.00 : endpointOnSegmentTol;
                        var yTol = seg.Priority == 0 ? 3.00 : endpointYLineTol;
                        var spanTol = seg.Priority == 0 ? 8.00 : xSpanTol;
                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > onSegmentTol)
                        {
                            continue;
                        }

                        var yLine = 0.5 * (seg.A.Y + seg.B.Y);
                        var yGap = Math.Abs(endpoint.Y - yLine);
                        if (yGap > yTol)
                        {
                            continue;
                        }

                        var minX = Math.Min(seg.A.X, seg.B.X);
                        var maxX = Math.Max(seg.A.X, seg.B.X);
                        if (endpoint.X < (minX - spanTol) || endpoint.X > (maxX + spanTol))
                        {
                            continue;
                        }

                        var move = Math.Abs(seg.Mid.X - endpoint.X);
                        if (move > maxMidpointShift)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            seg.Priority < bestPriority ||
                            (seg.Priority == bestPriority && segDistance < (bestSegDistance - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(segDistance - bestSegDistance) <= 1e-6 && yGap < (bestYGap - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(segDistance - bestSegDistance) <= 1e-6 && Math.Abs(yGap - bestYGap) <= 1e-6 && move < bestMove);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestPriority = seg.Priority;
                        bestSegDistance = segDistance;
                        bestYGap = yGap;
                        bestMove = move;
                        targetX = seg.Mid.X;
                        targetY = seg.Mid.Y;
                        targetPriority = seg.Priority;
                    }

                    return found;
                }

                bool TryResolveHorizontalQsecComponentMidpoint(Point2d endpoint, out double targetX, out double targetY)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    if (qsecHorizontalComponentTargets.Count == 0)
                    {
                        return false;
                    }

                    const double axisTol = 4.00;
                    const double spanTol = 40.0;
                    const double centerTol = 2.50;
                    const double maxMove = 80.0;
                    var found = false;
                    var bestAxisGap = double.MaxValue;
                    var bestMove = double.MaxValue;

                    for (var i = 0; i < qsecHorizontalComponentTargets.Count; i++)
                    {
                        var c = qsecHorizontalComponentTargets[i];
                        var yLine = 0.5 * (c.A.Y + c.B.Y);
                        var axisGap = Math.Abs(endpoint.Y - yLine);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        var minX = Math.Min(c.A.X, c.B.X);
                        var maxX = Math.Max(c.A.X, c.B.X);
                        if (endpoint.X < (minX - spanTol) || endpoint.X > (maxX + spanTol))
                        {
                            continue;
                        }

                        var resolvedOnComponent = false;
                        // Primary: midpoint between section-center and corresponding component endpoint.
                        // This guarantees all LSDs on the same 1/4 ray share one target.
                        for (var ci = 0; ci < qsecCenters.Count; ci++)
                        {
                            var center = qsecCenters[ci];
                            if (Math.Abs(center.Y - yLine) > centerTol ||
                                center.X < (minX - centerTol) ||
                                center.X > (maxX + centerTol))
                            {
                                continue;
                            }

                            var sideX = endpoint.X <= center.X
                                ? 0.5 * (minX + center.X)
                                : 0.5 * (maxX + center.X);
                            var move = Math.Abs(sideX - endpoint.X);
                            if (move <= midpointEndpointMoveTol || move > maxMove)
                            {
                                continue;
                            }

                            var betterFromCenter =
                                !found ||
                                axisGap < (bestAxisGap - 1e-6) ||
                                (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && move < bestMove);
                            if (!betterFromCenter)
                            {
                                continue;
                            }

                            found = true;
                            resolvedOnComponent = true;
                            bestAxisGap = axisGap;
                            bestMove = move;
                            targetX = sideX;
                            targetY = yLine;
                        }

                        if (resolvedOnComponent)
                        {
                            continue;
                        }

                        // Fallback when no center could be resolved for this component.
                        var fallbackMove = endpoint.GetDistanceTo(c.Mid);
                        if (fallbackMove <= midpointEndpointMoveTol || fallbackMove > maxMove)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            axisGap < (bestAxisGap - 1e-6) ||
                            (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && fallbackMove < bestMove);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestAxisGap = axisGap;
                        bestMove = fallbackMove;
                        targetX = c.Mid.X;
                        targetY = c.Mid.Y;
                    }

                    return found;
                }

                bool TryFindPairedVerticalMidpointX(Point2d p0, Point2d p1, out double targetX)
                {
                    targetX = 0.5 * (p0.X + p1.X);
                    var found0 = TryFindEndpointHorizontalMidpointX(p0, out var x0, out _, out var pri0);
                    var found1 = TryFindEndpointHorizontalMidpointX(p1, out var x1, out _, out var pri1);
                    if (!found0 && !found1)
                    {
                        return false;
                    }

                    // If only one endpoint resolves cleanly, use that midpoint so the line still
                    // lands on a valid half-line midpoint instead of drifting on a 30.18 corridor.
                    if (found0 && !found1)
                    {
                        targetX = x0;
                        return true;
                    }

                    if (found1 && !found0)
                    {
                        targetX = x1;
                        return true;
                    }

                    const double pairedXTol = 8.00;
                    if (Math.Abs(x0 - x1) > pairedXTol)
                    {
                        var currentX = 0.5 * (p0.X + p1.X);
                        if (pri0 < pri1)
                        {
                            targetX = x0;
                        }
                        else if (pri1 < pri0)
                        {
                            targetX = x1;
                        }
                        else
                        {
                            targetX = Math.Abs(x0 - currentX) <= Math.Abs(x1 - currentX) ? x0 : x1;
                        }

                        return true;
                    }

                    // Prefer the better-priority side if one is quarter-line anchored.
                    if (pri0 < pri1)
                    {
                        targetX = x0;
                    }
                    else if (pri1 < pri0)
                    {
                        targetX = x1;
                    }
                    else
                    {
                        targetX = 0.5 * (x0 + x1);
                    }

                    return true;
                }

                bool TryFindEndpointVerticalMidpointY(Point2d endpoint, out double targetY, out int targetPriority)
                {
                    targetY = endpoint.Y;
                    targetPriority = int.MaxValue;
                    const double endpointXLineTol = 2.00;
                    const double ySpanTol = 2.00;
                    const double endpointOnSegmentTol = 0.75;
                    const double maxMidpointShift = 40.0;

                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestXGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                    {
                        var seg = verticalMidpointTargetSegments[i];
                        var onSegmentTol = seg.Priority == 0 ? 2.00 : endpointOnSegmentTol;
                        var xTol = seg.Priority == 0 ? 3.00 : endpointXLineTol;
                        var spanTol = seg.Priority == 0 ? 8.00 : ySpanTol;
                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > onSegmentTol)
                        {
                            continue;
                        }

                        var xLine = 0.5 * (seg.A.X + seg.B.X);
                        var xGap = Math.Abs(endpoint.X - xLine);
                        if (xGap > xTol)
                        {
                            continue;
                        }

                        var minY = Math.Min(seg.A.Y, seg.B.Y);
                        var maxY = Math.Max(seg.A.Y, seg.B.Y);
                        if (endpoint.Y < (minY - spanTol) || endpoint.Y > (maxY + spanTol))
                        {
                            continue;
                        }

                        var move = Math.Abs(seg.Mid.Y - endpoint.Y);
                        if (move > maxMidpointShift)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            seg.Priority < bestPriority ||
                            (seg.Priority == bestPriority && segDistance < (bestSegDistance - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(segDistance - bestSegDistance) <= 1e-6 && xGap < (bestXGap - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(segDistance - bestSegDistance) <= 1e-6 && Math.Abs(xGap - bestXGap) <= 1e-6 && move < bestMove);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestPriority = seg.Priority;
                        bestSegDistance = segDistance;
                        bestXGap = xGap;
                        bestMove = move;
                        targetY = seg.Mid.Y;
                        targetPriority = seg.Priority;
                    }

                    return found;
                }

                bool TryResolveVerticalQsecComponentMidpoint(Point2d endpoint, out double targetX, out double targetY)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    if (qsecVerticalComponentTargets.Count == 0)
                    {
                        return false;
                    }

                    const double axisTol = 4.00;
                    const double spanTol = 40.0;
                    const double centerTol = 2.50;
                    const double maxMove = 80.0;
                    var found = false;
                    var bestAxisGap = double.MaxValue;
                    var bestMove = double.MaxValue;

                    for (var i = 0; i < qsecVerticalComponentTargets.Count; i++)
                    {
                        var c = qsecVerticalComponentTargets[i];
                        var xLine = 0.5 * (c.A.X + c.B.X);
                        var axisGap = Math.Abs(endpoint.X - xLine);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        var minY = Math.Min(c.A.Y, c.B.Y);
                        var maxY = Math.Max(c.A.Y, c.B.Y);
                        if (endpoint.Y < (minY - spanTol) || endpoint.Y > (maxY + spanTol))
                        {
                            continue;
                        }

                        var resolvedOnComponent = false;
                        for (var ci = 0; ci < qsecCenters.Count; ci++)
                        {
                            var center = qsecCenters[ci];
                            if (Math.Abs(center.X - xLine) > centerTol ||
                                center.Y < (minY - centerTol) ||
                                center.Y > (maxY + centerTol))
                            {
                                continue;
                            }

                            var sideY = endpoint.Y <= center.Y
                                ? 0.5 * (minY + center.Y)
                                : 0.5 * (maxY + center.Y);
                            var move = Math.Abs(sideY - endpoint.Y);
                            if (move <= midpointEndpointMoveTol || move > maxMove)
                            {
                                continue;
                            }

                            var betterFromCenter =
                                !found ||
                                axisGap < (bestAxisGap - 1e-6) ||
                                (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && move < bestMove);
                            if (!betterFromCenter)
                            {
                                continue;
                            }

                            found = true;
                            resolvedOnComponent = true;
                            bestAxisGap = axisGap;
                            bestMove = move;
                            targetX = xLine;
                            targetY = sideY;
                        }

                        if (resolvedOnComponent)
                        {
                            continue;
                        }

                        var fallbackMove = endpoint.GetDistanceTo(c.Mid);
                        if (fallbackMove <= midpointEndpointMoveTol || fallbackMove > maxMove)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            axisGap < (bestAxisGap - 1e-6) ||
                            (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && fallbackMove < bestMove);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestAxisGap = axisGap;
                        bestMove = fallbackMove;
                        targetX = c.Mid.X;
                        targetY = c.Mid.Y;
                    }

                    return found;
                }

                bool TryFindPairedHorizontalMidpointY(Point2d p0, Point2d p1, out double targetY)
                {
                    targetY = 0.5 * (p0.Y + p1.Y);
                    var found0 = TryFindEndpointVerticalMidpointY(p0, out var y0, out var pri0);
                    var found1 = TryFindEndpointVerticalMidpointY(p1, out var y1, out var pri1);
                    if (!found0 && !found1)
                    {
                        return false;
                    }

                    if (found0 && !found1)
                    {
                        targetY = y0;
                        return true;
                    }

                    if (found1 && !found0)
                    {
                        targetY = y1;
                        return true;
                    }

                    const double pairedYTol = 8.00;
                    if (Math.Abs(y0 - y1) > pairedYTol)
                    {
                        var currentY = 0.5 * (p0.Y + p1.Y);
                        if (pri0 < pri1)
                        {
                            targetY = y0;
                        }
                        else if (pri1 < pri0)
                        {
                            targetY = y1;
                        }
                        else
                        {
                            targetY = Math.Abs(y0 - currentY) <= Math.Abs(y1 - currentY) ? y0 : y1;
                        }

                        return true;
                    }

                    if (pri0 < pri1)
                    {
                        targetY = y0;
                    }
                    else if (pri1 < pri0)
                    {
                        targetY = y1;
                    }
                    else
                    {
                        targetY = 0.5 * (y0 + y1);
                    }

                    return true;
                }

                bool TryFindSnapTarget(Point2d endpoint, Point2d other, out Point2d target)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceHorizontal = IsHorizontalLike(other, endpoint);
                    var sourceVertical = IsVerticalLike(other, endpoint);
                    if (!sourceHorizontal && !sourceVertical)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var bestScore = double.MaxValue;
                    var bestTarget = endpoint;
                    var perpDir = new Vector2d(-outwardDir.Y, outwardDir.X);
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                        var segVertical = IsVerticalLike(seg.A, seg.B);

                        // LSD endpoints should land on midpoint of orthogonal hard boundary.
                        if (sourceHorizontal && !segVertical)
                        {
                            continue;
                        }

                        if (sourceVertical && !segHorizontal)
                        {
                            continue;
                        }

                        var midpoint = Midpoint(seg.A, seg.B);
                        var delta = midpoint - endpoint;
                        var move = delta.Length;
                        if (move <= minMove || move > maxMove)
                        {
                            continue;
                        }

                        // Keep midpoint candidates aligned to the current LSD axis.
                        var lateral = Math.Abs(delta.DotProduct(perpDir));
                        if (lateral > midpointAxisTol)
                        {
                            continue;
                        }

                        // Keep endpoint on the same outward side from the opposite LSD endpoint.
                        var projectedFromOther = (midpoint - other).DotProduct(outwardDir);
                        if (projectedFromOther < minRemainingLength)
                        {
                            continue;
                        }

                        var score = (lateral * 100.0) + move;
                        if (score >= bestScore)
                        {
                            continue;
                        }

                        bestScore = score;
                        bestTarget = midpoint;
                        found = true;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = bestTarget;
                    return true;
                }

                bool TryFindNearestUsecMidpoint(Point2d endpoint, bool? preferZero, out Point2d target)
                {
                    target = endpoint;
                    var foundPreferred = false;
                    var bestPreferredMove = double.MaxValue;
                    var bestPreferredTarget = endpoint;
                    var foundFallback = false;
                    var bestFallbackMove = double.MaxValue;
                    var bestFallbackTarget = endpoint;
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var midpoint = Midpoint(seg.A, seg.B);
                        var move = endpoint.GetDistanceTo(midpoint);
                        if (move <= minMove || move > maxMove)
                        {
                            continue;
                        }

                        var isPreferred = !preferZero.HasValue || seg.IsZero == preferZero.Value;
                        if (isPreferred)
                        {
                            if (move >= bestPreferredMove)
                            {
                                continue;
                            }

                            bestPreferredMove = move;
                            bestPreferredTarget = midpoint;
                            foundPreferred = true;
                        }
                        else
                        {
                            if (move >= bestFallbackMove)
                            {
                                continue;
                            }

                            bestFallbackMove = move;
                            bestFallbackTarget = midpoint;
                            foundFallback = true;
                        }
                    }

                    if (foundPreferred)
                    {
                        target = bestPreferredTarget;
                        return true;
                    }

                    if (foundFallback)
                    {
                        target = bestFallbackTarget;
                        return true;
                    }

                    return false;
                }

                bool TryFindNearestHardBoundaryPoint(Point2d endpoint, Point2d other, bool? preferZero, out Point2d target)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceHorizontal = IsHorizontalLike(other, endpoint);
                    var sourceVertical = IsVerticalLike(other, endpoint);
                    if (!sourceHorizontal && !sourceVertical)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var perpDir = new Vector2d(-outwardDir.Y, outwardDir.X);
                    var foundPreferred = false;
                    var bestPreferredScore = double.MaxValue;
                    var bestPreferredTarget = endpoint;
                    var foundFallback = false;
                    var bestFallbackScore = double.MaxValue;
                    var bestFallbackTarget = endpoint;

                    Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
                    {
                        var ab = b - a;
                        var len2 = ab.DotProduct(ab);
                        if (len2 <= 1e-12)
                        {
                            return a;
                        }

                        var ap = p - a;
                        var t = ap.DotProduct(ab) / len2;
                        if (t < 0.0) t = 0.0;
                        if (t > 1.0) t = 1.0;
                        return new Point2d(a.X + (ab.X * t), a.Y + (ab.Y * t));
                    }

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                        var segVertical = IsVerticalLike(seg.A, seg.B);
                        if (sourceHorizontal && !segVertical)
                        {
                            continue;
                        }

                        if (sourceVertical && !segHorizontal)
                        {
                            continue;
                        }

                        var candidate = ClosestPointOnSegment(endpoint, seg.A, seg.B);
                        var delta = candidate - endpoint;
                        var move = delta.Length;
                        if (move <= minMove || move > maxMove)
                        {
                            continue;
                        }

                        var lateral = Math.Abs(delta.DotProduct(perpDir));
                        if (lateral > midpointAxisTol)
                        {
                            continue;
                        }

                        var projectedFromOther = (candidate - other).DotProduct(outwardDir);
                        if (projectedFromOther < minRemainingLength)
                        {
                            continue;
                        }

                        var score = (lateral * 100.0) + move;
                        var isPreferred = !preferZero.HasValue || seg.IsZero == preferZero.Value;
                        if (isPreferred)
                        {
                            if (score >= bestPreferredScore)
                            {
                                continue;
                            }

                            bestPreferredScore = score;
                            bestPreferredTarget = candidate;
                            foundPreferred = true;
                        }
                        else
                        {
                            if (score >= bestFallbackScore)
                            {
                                continue;
                            }

                            bestFallbackScore = score;
                            bestFallbackTarget = candidate;
                            foundFallback = true;
                        }
                    }

                    if (foundPreferred)
                    {
                        target = bestPreferredTarget;
                        return true;
                    }

                    if (foundFallback)
                    {
                        target = bestFallbackTarget;
                        return true;
                    }

                    return false;
                }

                for (var i = 0; i < lsdLineIds.Count; i++)
                {
                    var id = lsdLineIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;
                    var midpointLockedStart = false;
                    var midpointLockedEnd = false;

                    // Midpoint special case:
                    // anchor LSD endpoints to the midpoint of the line they terminate on
                    // (1/4, blind, or section) before generic hard-boundary snapping.
                    if (IsVerticalLike(p0, p1))
                    {
                        var hasStartMid = false;
                        var hasEndMid = false;
                        var midStartX = p0.X;
                        var midEndX = p1.X;
                        var midStartY = p0.Y;
                        var midEndY = p1.Y;

                        // Deterministic rule: if endpoints terminate on the same 1/4 component,
                        // they must resolve to the same component midpoint.
                        hasStartMid = TryResolveHorizontalQsecComponentMidpoint(p0, out midStartX, out midStartY);
                        hasEndMid = TryResolveHorizontalQsecComponentMidpoint(p1, out midEndX, out midEndY);
                        if (!hasStartMid)
                        {
                            hasStartMid = TryFindEndpointHorizontalMidpointX(p0, out midStartX, out midStartY, out _);
                        }

                        if (!hasEndMid)
                        {
                            hasEndMid = TryFindEndpointHorizontalMidpointX(p1, out midEndX, out midEndY, out _);
                        }

                        if (hasStartMid && hasEndMid)
                        {
                            // Keep both vertical endpoints on their own target midpoints.
                            // Only force a shared X when both midpoint targets are already effectively identical.
                            const double pairedVerticalMidpointXTol = 0.25;
                            if (Math.Abs(midStartX - midEndX) <= pairedVerticalMidpointXTol)
                            {
                                var sharedX = 0.5 * (midStartX + midEndX);
                                midStartX = sharedX;
                                midEndX = sharedX;
                            }
                        }

                        if (hasStartMid || hasEndMid)
                        {
                            if (hasStartMid)
                            {
                                var snappedStart = new Point2d(midStartX, midStartY);
                                if (p0.GetDistanceTo(snappedStart) > midpointEndpointMoveTol)
                                {
                                    moveStart = true;
                                    targetStart = snappedStart;
                                }
                            }

                            if (hasEndMid)
                            {
                                var snappedEnd = new Point2d(midEndX, midEndY);
                                if (p1.GetDistanceTo(snappedEnd) > midpointEndpointMoveTol)
                                {
                                    moveEnd = true;
                                    targetEnd = snappedEnd;
                                }
                            }

                            if (moveStart || moveEnd)
                            {
                                var movedByMidpoint = false;
                                if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, midpointEndpointMoveTol))
                                {
                                    adjustedEndpoints++;
                                    midpointAdjustedEndpoints++;
                                    movedByMidpoint = true;
                                }

                                if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, midpointEndpointMoveTol))
                                {
                                    adjustedEndpoints++;
                                    midpointAdjustedEndpoints++;
                                    movedByMidpoint = true;
                                }

                                if (movedByMidpoint)
                                {
                                    adjustedLines++;
                                    midpointAdjustedLines++;
                                }

                                if (!TryReadOpenSegment(writable, out p0, out p1))
                                {
                                    continue;
                                }
                            }

                            midpointLockedStart = hasStartMid;
                            midpointLockedEnd = hasEndMid;
                            moveStart = false;
                            moveEnd = false;
                            targetStart = p0;
                            targetEnd = p1;
                        }
                    }
                    else if (IsHorizontalLike(p0, p1))
                    {
                        var hasStartMid = false;
                        var hasEndMid = false;
                        var midStartY = p0.Y;
                        var midEndY = p1.Y;

                        var hasStartQsecVertical = TryResolveVerticalQsecComponentMidpoint(p0, out _, out var qsecStartY);
                        var hasEndQsecVertical = TryResolveVerticalQsecComponentMidpoint(p1, out _, out var qsecEndY);
                        if (hasStartQsecVertical || hasEndQsecVertical)
                        {
                            hasStartMid = hasStartQsecVertical;
                            hasEndMid = hasEndQsecVertical;
                            midStartY = qsecStartY;
                            midEndY = qsecEndY;
                        }
                        else if (TryFindPairedHorizontalMidpointY(p0, p1, out var pairedY))
                        {
                            hasStartMid = true;
                            hasEndMid = true;
                            midStartY = pairedY;
                            midEndY = pairedY;
                        }
                        else
                        {
                            hasStartMid = TryFindEndpointVerticalMidpointY(p0, out midStartY, out _);
                            hasEndMid = TryFindEndpointVerticalMidpointY(p1, out midEndY, out _);
                        }

                        if (hasStartMid || hasEndMid)
                        {
                            if (hasStartMid)
                            {
                                var snappedStart = new Point2d(p0.X, midStartY);
                                if (p0.GetDistanceTo(snappedStart) > midpointEndpointMoveTol)
                                {
                                    moveStart = true;
                                    targetStart = snappedStart;
                                }
                            }

                            if (hasEndMid)
                            {
                                var snappedEnd = new Point2d(p1.X, midEndY);
                                if (p1.GetDistanceTo(snappedEnd) > midpointEndpointMoveTol)
                                {
                                    moveEnd = true;
                                    targetEnd = snappedEnd;
                                }
                            }

                            if (moveStart || moveEnd)
                            {
                                var movedByMidpoint = false;
                                if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, midpointEndpointMoveTol))
                                {
                                    adjustedEndpoints++;
                                    midpointAdjustedEndpoints++;
                                    movedByMidpoint = true;
                                }

                                if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, midpointEndpointMoveTol))
                                {
                                    adjustedEndpoints++;
                                    midpointAdjustedEndpoints++;
                                    movedByMidpoint = true;
                                }

                                if (movedByMidpoint)
                                {
                                    adjustedLines++;
                                    midpointAdjustedLines++;
                                }

                                if (!TryReadOpenSegment(writable, out p0, out p1))
                                {
                                    continue;
                                }
                            }

                            midpointLockedStart = hasStartMid;
                            midpointLockedEnd = hasEndMid;
                            moveStart = false;
                            moveEnd = false;
                            targetStart = p0;
                            targetEnd = p1;
                        }
                    }

                    if (midpointLockedStart && midpointLockedEnd)
                    {
                        continue;
                    }

                    if (!midpointLockedStart)
                    {
                        scannedEndpoints++;
                        var p0OnZero = IsEndpointOnUsecZeroBoundary(p0);
                        var p0OnTwenty = IsEndpointOnUsecTwentyBoundary(p0);
                        var p0OnThirty = IsEndpointOnThirtyBoundary(p0);
                        if (p0OnZero)
                        {
                            alreadyOnHardBoundary++;
                        }
                        else if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol) && !p0OnThirty)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            if (p0OnThirty)
                            {
                                onThirtyOnly++;
                            }

                            var snappedStart = p0;
                            var foundStartTarget = false;
                            if (p0OnThirty)
                            {
                                bool? preferZero = null;
                                if (IsHorizontalLike(p0, p1))
                                {
                                    // Horizontal LSD side rule: right endpoint -> 0, left endpoint -> 20.12.
                                    preferZero = p0.X > p1.X;
                                }

                                foundStartTarget =
                                    TryFindNearestHardBoundaryPoint(p0, p1, preferZero, out snappedStart) ||
                                    TryFindNearestUsecMidpoint(p0, preferZero, out snappedStart);
                            }
                            else
                            {
                                foundStartTarget = TryFindSnapTarget(p0, p1, out snappedStart);
                            }
                            if (foundStartTarget)
                            {
                                if (p0.GetDistanceTo(snappedStart) <= endpointTouchTol)
                                {
                                    alreadyOnHardBoundary++;
                                }
                                else
                                {
                                    moveStart = true;
                                    targetStart = snappedStart;
                                }
                            }
                            else if (p0OnTwenty || IsEndpointOnUsecTwentyBoundary(p0))
                            {
                                alreadyOnHardBoundary++;
                            }
                            else if (IsEndpointOnHardBoundary(p0))
                            {
                                alreadyOnHardBoundary++;
                            }
                            else
                            {
                                noTarget++;
                            }
                        }
                    }

                    if (!midpointLockedEnd)
                    {
                        scannedEndpoints++;
                        var p1OnZero = IsEndpointOnUsecZeroBoundary(p1);
                        var p1OnTwenty = IsEndpointOnUsecTwentyBoundary(p1);
                        var p1OnThirty = IsEndpointOnThirtyBoundary(p1);
                        if (p1OnZero)
                        {
                            alreadyOnHardBoundary++;
                        }
                        else if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol) && !p1OnThirty)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            if (p1OnThirty)
                            {
                                onThirtyOnly++;
                            }

                            var snappedEnd = p1;
                            var foundEndTarget = false;
                            if (p1OnThirty)
                            {
                                bool? preferZero = null;
                                if (IsHorizontalLike(p0, p1))
                                {
                                    // Horizontal LSD side rule: right endpoint -> 0, left endpoint -> 20.12.
                                    preferZero = p1.X > p0.X;
                                }

                                foundEndTarget =
                                    TryFindNearestHardBoundaryPoint(p1, p0, preferZero, out snappedEnd) ||
                                    TryFindNearestUsecMidpoint(p1, preferZero, out snappedEnd);
                            }
                            else
                            {
                                foundEndTarget = TryFindSnapTarget(p1, p0, out snappedEnd);
                            }
                            if (foundEndTarget)
                            {
                                if (p1.GetDistanceTo(snappedEnd) <= endpointTouchTol)
                                {
                                    alreadyOnHardBoundary++;
                                }
                                else
                                {
                                    moveEnd = true;
                                    targetEnd = snappedEnd;
                                }
                            }
                            else if (p1OnTwenty || IsEndpointOnUsecTwentyBoundary(p1))
                            {
                                alreadyOnHardBoundary++;
                            }
                            else if (IsEndpointOnHardBoundary(p1))
                            {
                                alreadyOnHardBoundary++;
                            }
                            else
                            {
                                noTarget++;
                            }
                        }
                    }

                    if (moveStart && moveEnd && targetStart.GetDistanceTo(targetEnd) < minRemainingLength)
                    {
                        var startMoveDist = p0.GetDistanceTo(targetStart);
                        var endMoveDist = p1.GetDistanceTo(targetEnd);
                        if (startMoveDist >= endMoveDist)
                        {
                            moveEnd = false;
                        }
                        else
                        {
                            moveStart = false;
                        }
                    }

                    if (!moveStart && !moveEnd)
                    {
                        continue;
                    }

                    var movedLine = false;
                    if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (movedLine)
                    {
                        adjustedLines++;
                    }
                }

                // Final clamp:
                // if an LSD endpoint already terminates on an L-QSEC component, force it to that
                // full-component midpoint (not a split-fragment midpoint).
                if (lsdLineIds.Count > 0 && (qsecHorizontalComponentTargets.Count > 0 || qsecVerticalComponentTargets.Count > 0))
                {
                    bool TryFindHorizontalQsecComponentMidpoint(Point2d endpoint, out Point2d target)
                    {
                        target = endpoint;
                        if (!TryResolveHorizontalQsecComponentMidpoint(endpoint, out var tx, out var ty))
                        {
                            return false;
                        }

                        target = new Point2d(tx, ty);
                        return true;
                    }

                    bool TryFindVerticalQsecComponentMidpoint(Point2d endpoint, out Point2d target)
                    {
                        target = endpoint;
                        if (!TryResolveVerticalQsecComponentMidpoint(endpoint, out var tx, out var ty))
                        {
                            return false;
                        }

                        target = new Point2d(tx, ty);
                        return true;
                    }

                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var p0, out var p1))
                        {
                            continue;
                        }

                        var movedAny = false;
                        if (IsVerticalLike(p0, p1))
                        {
                            var moveStart = false;
                            var moveEnd = false;
                            var targetStart = p0;
                            var targetEnd = p1;
                            if (TryFindHorizontalQsecComponentMidpoint(p0, out var t0))
                            {
                                moveStart = true;
                                targetStart = t0;
                            }

                            if (TryFindHorizontalQsecComponentMidpoint(p1, out var t1))
                            {
                                moveEnd = true;
                                targetEnd = t1;
                            }

                            if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, midpointEndpointMoveTol))
                            {
                                qsecComponentClampEndpoints++;
                                adjustedEndpoints++;
                                movedAny = true;
                            }

                            if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, midpointEndpointMoveTol))
                            {
                                qsecComponentClampEndpoints++;
                                adjustedEndpoints++;
                                movedAny = true;
                            }
                        }
                        else if (IsHorizontalLike(p0, p1))
                        {
                            var moveStart = false;
                            var moveEnd = false;
                            var targetStart = p0;
                            var targetEnd = p1;
                            if (TryFindVerticalQsecComponentMidpoint(p0, out var t0))
                            {
                                moveStart = true;
                                targetStart = t0;
                            }

                            if (TryFindVerticalQsecComponentMidpoint(p1, out var t1))
                            {
                                moveEnd = true;
                                targetEnd = t1;
                            }

                            if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, midpointEndpointMoveTol))
                            {
                                qsecComponentClampEndpoints++;
                                adjustedEndpoints++;
                                movedAny = true;
                            }

                            if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, midpointEndpointMoveTol))
                            {
                                qsecComponentClampEndpoints++;
                                adjustedEndpoints++;
                                movedAny = true;
                            }
                        }

                        if (movedAny)
                        {
                            qsecComponentClampLines++;
                            adjustedLines++;
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: LSD hard-boundary rule scanned={scannedEndpoints}, alreadyOnHard={alreadyOnHardBoundary}, on30Only={onThirtyOnly}, midpointLines={midpointAdjustedLines}, midpointEndpoints={midpointAdjustedEndpoints}, qsecComponentClampLines={qsecComponentClampLines}, qsecComponentClampEndpoints={qsecComponentClampEndpoints}, windowBoundarySkipped={boundarySkipped}, noTarget={noTarget}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
            }
        }

        private static void EnforceBlindLineEndpointsOnSectionBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
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

            bool IsBlindSourceLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsHardBoundaryLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                // Accept both canonical and alias names used in old files/log notes.
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsThirtyEighteenLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blindSourceIds = new List<ObjectId>();
                var hardBoundarySegments = new List<(Point2d A, Point2d B)>();
                var thirtyBoundarySegments = new List<(Point2d A, Point2d B)>();
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

                    var layer = ent.Layer ?? string.Empty;
                    if (IsHardBoundaryLayer(layer))
                    {
                        hardBoundarySegments.Add((a, b));
                        continue;
                    }

                    if (IsThirtyEighteenLayer(layer))
                    {
                        thirtyBoundarySegments.Add((a, b));
                        continue;
                    }

                    if (!IsBlindSourceLayer(layer))
                    {
                        continue;
                    }

                    blindSourceIds.Add(id);
                }

                if (blindSourceIds.Count == 0 || hardBoundarySegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTouchTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double minExtend = 0.05;
                const double maxExtend = 11.0;
                const double minRemainingLength = 2.0;
                const double outerBoundaryTol = 0.40;

                var scannedEndpoints = 0;
                var alreadyOnHard = 0;
                var onThirtyOnly = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;

                bool IsEndpointOnHardBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsEndpointOnThirtyOnly(Point2d endpoint)
                {
                    for (var i = 0; i < thirtyBoundarySegments.Count; i++)
                    {
                        var seg = thirtyBoundarySegments[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindExtensionTarget(Point2d endpoint, Point2d other, out Point2d target)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var bestT = double.MaxValue;
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t))
                        {
                            continue;
                        }

                        // Extension only: move outward from this endpoint.
                        if (t <= minExtend || t > maxExtend)
                        {
                            continue;
                        }

                        if (t >= bestT)
                        {
                            continue;
                        }

                        found = true;
                        bestT = t;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = endpoint + (outwardDir * bestT);
                    return true;
                }

                for (var i = 0; i < blindSourceIds.Count; i++)
                {
                    var sourceId = blindSourceIds[i];
                    if (!(tr.GetObject(sourceId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;

                    scannedEndpoints++;
                    if (IsEndpointOnHardBoundary(p0))
                    {
                        alreadyOnHard++;
                    }
                    else if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else
                    {
                        if (IsEndpointOnThirtyOnly(p0))
                        {
                            onThirtyOnly++;
                        }

                        if (TryFindExtensionTarget(p0, p1, out var snappedStart))
                        {
                            moveStart = true;
                            targetStart = snappedStart;
                        }
                        else
                        {
                            noTarget++;
                        }
                    }

                    scannedEndpoints++;
                    if (IsEndpointOnHardBoundary(p1))
                    {
                        alreadyOnHard++;
                    }
                    else if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else
                    {
                        if (IsEndpointOnThirtyOnly(p1))
                        {
                            onThirtyOnly++;
                        }

                        if (TryFindExtensionTarget(p1, p0, out var snappedEnd))
                        {
                            moveEnd = true;
                            targetEnd = snappedEnd;
                        }
                        else
                        {
                            noTarget++;
                        }
                    }

                    if (moveStart && moveEnd && targetStart.GetDistanceTo(targetEnd) < minRemainingLength)
                    {
                        var startMoveDist = p0.GetDistanceTo(targetStart);
                        var endMoveDist = p1.GetDistanceTo(targetEnd);
                        if (startMoveDist >= endMoveDist)
                        {
                            moveEnd = false;
                        }
                        else
                        {
                            moveStart = false;
                        }
                    }

                    if (!moveStart && !moveEnd)
                    {
                        continue;
                    }

                    var movedLine = false;
                    if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                    }

                    if (movedLine)
                    {
                        adjustedLines++;
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: blind-line 11m hard-boundary extend scannedEndpoints={scannedEndpoints}, alreadyOnHard={alreadyOnHard}, on30Only={onThirtyOnly}, windowBoundarySkipped={boundarySkipped}, noTarget={noTarget}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}, maxExtend=11m.");
            }
        }

        private static void CleanupDuplicateBlindLineSegments(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, Point2d Mid, double Len)>();
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

                    var isUsec = IsUsecLayer(ent.Layer ?? string.Empty);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
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

                    var len = a.GetDistanceTo(b);
                    if (len < 2.0 || len > 2000.0)
                    {
                        continue;
                    }

                    if (!IsHorizontalLike(a, b) && !IsVerticalLike(a, b))
                    {
                        continue;
                    }

                    candidates.Add((id, ent.Layer, a, b, Midpoint(a, b), len));
                }

                if (candidates.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTol = 0.75;
                const double midpointTol = 0.60;
                const double lengthTol = 0.60;
                const double minBlindLen = 8.0;
                const double maxBlindLen = 2000.0;
                const double containTol = 0.60;

                bool IsSegmentContained(Point2d innerA, Point2d innerB, Point2d outerA, Point2d outerB)
                {
                    return DistancePointToSegment(innerA, outerA, outerB) <= containTol &&
                           DistancePointToSegment(innerB, outerA, outerB) <= containTol;
                }

                var toErase = new HashSet<ObjectId>();
                for (var i = 0; i < candidates.Count; i++)
                {
                    var a = candidates[i];
                    if (toErase.Contains(a.Id))
                    {
                        continue;
                    }

                    if (a.Len < minBlindLen || a.Len > maxBlindLen)
                    {
                        continue;
                    }

                    for (var j = i + 1; j < candidates.Count; j++)
                    {
                        var b = candidates[j];
                        if (toErase.Contains(b.Id))
                        {
                            continue;
                        }

                        if (b.Len < minBlindLen || b.Len > maxBlindLen)
                        {
                            continue;
                        }

                        var nearDuplicate = AreSegmentEndpointsNear(a.A, a.B, b.A, b.B, endpointTol);
                        if (!nearDuplicate)
                        {
                            var collinearOverlap = AreSegmentsDuplicateOrCollinearOverlap(a.A, a.B, b.A, b.B);
                            if (!collinearOverlap)
                            {
                                continue;
                            }

                            var similarShape =
                                Math.Abs(a.Len - b.Len) <= lengthTol &&
                                a.Mid.GetDistanceTo(b.Mid) <= midpointTol;
                            var contained =
                                IsSegmentContained(a.A, a.B, b.A, b.B) ||
                                IsSegmentContained(b.A, b.B, a.A, a.B);
                            if (!similarShape && !contained)
                            {
                                continue;
                            }
                        }

                        var eraseId = a.Id.Handle.Value > b.Id.Handle.Value ? a.Id : b.Id;
                        var aUsec = string.Equals(a.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        var bUsec = string.Equals(b.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        if (aUsec != bUsec)
                        {
                            // Prefer keeping L-USEC when duplicates were emitted on mixed layers.
                            eraseId = aUsec ? b.Id : a.Id;
                        }

                        toErase.Add(eraseId);
                    }
                }

                var erased = 0;
                foreach (var id in toErase)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Erase();
                    erased++;
                }

                tr.Commit();
                if (erased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {erased} duplicate blind-line segment(s) on adjoining sections.");
                }
            }
        }
    }
}
