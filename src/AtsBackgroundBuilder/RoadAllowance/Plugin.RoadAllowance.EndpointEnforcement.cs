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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForEndpointEnforcement(p, tol, clipWindows);

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);


            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

            bool IsSecSourceLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsHardBoundaryLayer(string layer) =>
                IsHardBoundaryLayerForEndpointEnforcement(layer, includeSecAliases: false);

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

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b))
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

                bool IsEndpointOnHardBoundary(Point2d endpoint, ObjectId sourceId) =>
                    IsEndpointOnBoundarySegmentsForEndpointEnforcement(
                        endpoint,
                        sourceId,
                        hardBoundarySegments,
                        endpointTouchTol);

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

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
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

        private static void EnforceZeroTwentyEndpointsOnCorrectionZeroBoundaries(
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForEndpointEnforcement(p, tol, clipWindows);

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

            bool IsZeroTwentySourceLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsCorrectionZeroBoundaryLayer(string layer)
            {
                return string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var correctionZeroSegments = new List<(LineDistancePoint A, LineDistancePoint B)>();
                var sourceIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsCorrectionZeroBoundaryLayer(layer))
                    {
                        correctionZeroSegments.Add(
                            (new LineDistancePoint(a.X, a.Y), new LineDistancePoint(b.X, b.Y)));
                        continue;
                    }

                    if (IsZeroTwentySourceLayer(layer))
                    {
                        sourceIds.Add(id);
                    }
                }

                if (correctionZeroSegments.Count == 0 || sourceIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTouchTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double minExtend = 0.05;
                const double maxExtend = 8.0;
                const double insetToleranceMeters = 1.0;
                const double minRemainingLength = 2.0;
                const double outerBoundaryTol = 0.40;

                var scannedEndpoints = 0;
                var alreadyOnCorrectionZero = 0;
                var windowBoundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;

                bool IsEndpointOnCorrectionZero(Point2d endpoint)
                {
                    for (var i = 0; i < correctionZeroSegments.Count; i++)
                    {
                        var seg = correctionZeroSegments[i];
                        if (DistancePointToSegment(
                                endpoint,
                                new Point2d(seg.A.X, seg.A.Y),
                                new Point2d(seg.B.X, seg.B.Y)) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryProjectCorrectionZeroTarget(Point2d endpoint, Point2d otherEndpoint, out Point2d target)
                {
                    target = endpoint;
                    if (!CorrectionZeroCompanionProjection.TryProjectCompanionTarget(
                            endpoint: new LineDistancePoint(endpoint.X, endpoint.Y),
                            otherEndpoint: new LineDistancePoint(otherEndpoint.X, otherEndpoint.Y),
                            ordinaryTarget: new LineDistancePoint(endpoint.X, endpoint.Y),
                            correctionZeroSegments,
                            CorrectionLineInsetMeters,
                            insetToleranceMeters,
                            minExtend,
                            maxExtend,
                            out var correctionTarget))
                    {
                        return false;
                    }

                    target = new Point2d(correctionTarget.X, correctionTarget.Y);
                    return true;
                }

                for (var i = 0; i < sourceIds.Count; i++)
                {
                    var sourceId = sourceIds[i];
                    if (!(tr.GetObject(sourceId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;

                    scannedEndpoints++;
                    if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol))
                    {
                        windowBoundarySkipped++;
                    }
                    else if (IsEndpointOnCorrectionZero(p0))
                    {
                        alreadyOnCorrectionZero++;
                    }
                    else if (TryProjectCorrectionZeroTarget(p0, p1, out var snappedStart))
                    {
                        moveStart = true;
                        targetStart = snappedStart;
                    }
                    else
                    {
                        noTarget++;
                    }

                    scannedEndpoints++;
                    if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol))
                    {
                        windowBoundarySkipped++;
                    }
                    else if (IsEndpointOnCorrectionZero(p1))
                    {
                        alreadyOnCorrectionZero++;
                    }
                    else if (TryProjectCorrectionZeroTarget(p1, p0, out var snappedEnd))
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
                if (scannedEndpoints > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: 0/20 correction-zero companion snap scannedEndpoints={scannedEndpoints}, alreadyOnCorrectionZero={alreadyOnCorrectionZero}, windowBoundarySkipped={windowBoundarySkipped}, noTarget={noTarget}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
                }
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForEndpointEnforcement(p, tol, clipWindows);

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);


            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

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
                var correctionBoundarySegments = new List<(Point2d A, Point2d B)>();
                var qsecLineIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b))
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
                        if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            correctionBoundarySegments.Add((a, b));
                        }

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
                const double correctionAdjTol = 60.0;
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

                bool IsEndpointNearCorrectionBoundary(Point2d endpoint) =>
                    IsEndpointNearBoundarySegmentsForEndpointEnforcement(endpoint, correctionBoundarySegments, correctionAdjTol);

                bool TryIntersectInfiniteLineWithBoundedSegmentExtension(
                    Point2d linePoint,
                    Vector2d lineDir,
                    Point2d segA,
                    Point2d segB,
                    double maxSegmentExtension,
                    out double tOnLine)
                {
                    tOnLine = 0.0;
                    var segDir = segB - segA;
                    var segLen = segDir.Length;
                    if (segLen <= 1e-9)
                    {
                        return false;
                    }

                    var denom = Cross2d(lineDir, segDir);
                    if (Math.Abs(denom) <= 1e-9)
                    {
                        return false;
                    }

                    var diff = segA - linePoint;
                    var t = Cross2d(diff, segDir) / denom;
                    var u = Cross2d(diff, lineDir) / denom;

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

                    tOnLine = t;
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

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var bestAbsT = double.MaxValue;
                    var bestT = 0.0;
                    var bestIsFallback = true;
                    const double apparentSegmentExtensionTol = 6.0;
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
                        if (TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t))
                        {
                            ConsiderCandidate(t, isFallback: false);
                            continue;
                        }

                        if (TryIntersectInfiniteLineWithBoundedSegmentExtension(
                            endpoint,
                            outwardDir,
                            seg.A,
                            seg.B,
                            apparentSegmentExtensionTol,
                            out var apparentT))
                        {
                            // Apparent intersection fallback for tiny boundary truncations.
                            ConsiderCandidate(apparentT, isFallback: true);
                        }
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

                bool TryFindCorrectionAdjacentSnapTarget(
                    Point2d endpoint,
                    Point2d other,
                    out Point2d target)
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
                    var bestU = double.MaxValue;
                    var bestIsFallback = true;
                    const double apparentSegmentExtensionTol = 6.0;
                    void ConsiderCandidate(double u, bool isFallback)
                    {
                        if (u < minRemainingLength)
                        {
                            return;
                        }

                        var t = u - outwardLen;
                        var absT = Math.Abs(t);
                        if (absT <= minMove || absT > maxMove)
                        {
                            return;
                        }

                        var isBetter =
                            !found ||
                            u < (bestU - 1e-6) ||
                            (Math.Abs(u - bestU) <= 1e-6 && bestIsFallback && !isFallback);
                        if (!isBetter)
                        {
                            return;
                        }

                        found = true;
                        bestU = u;
                        bestIsFallback = isFallback;
                    }

                    void ScanSegments(List<(Point2d A, Point2d B)> segments)
                    {
                        for (var i = 0; i < segments.Count; i++)
                        {
                            var seg = segments[i];
                            if (TryIntersectInfiniteLineWithSegment(other, outwardDir, seg.A, seg.B, out var u))
                            {
                                ConsiderCandidate(u, isFallback: false);
                                continue;
                            }

                            if (TryIntersectInfiniteLineWithBoundedSegmentExtension(
                                other,
                                outwardDir,
                                seg.A,
                                seg.B,
                                apparentSegmentExtensionTol,
                                out var apparentU))
                            {
                                // Apparent intersection fallback for tiny boundary truncations.
                                ConsiderCandidate(apparentU, isFallback: true);
                            }
                        }

                        for (var i = 0; i < segments.Count; i++)
                        {
                            var seg = segments[i];
                            for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                            {
                                var candidate = endpointIndex == 0 ? seg.A : seg.B;
                                if (DistancePointToInfiniteLine(candidate, other, other + outwardDir) > endpointAxisTol)
                                {
                                    continue;
                                }

                                var u = (candidate - other).DotProduct(outwardDir);
                                ConsiderCandidate(u, isFallback: true);
                            }
                        }
                    }

                    // Correction-adjacent rule: scan correction boundaries first, but if the best
                    // correction candidate is effectively the current endpoint (no meaningful move),
                    // continue and allow generic hard boundaries to provide the actual projected hit.
                    ScanSegments(correctionBoundarySegments);
                    var correctionCandidateIsCurrent =
                        found && Math.Abs(bestU - outwardLen) <= endpointTouchTol;
                    if (!found || correctionCandidateIsCurrent)
                    {
                        ScanSegments(boundarySegments);
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = other + (outwardDir * bestU);
                    return true;
                }

                for (var i = 0; i < qsecLineIds.Count; i++)
                {
                    var id = qsecLineIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;
                    var p0CorrectionAdjacent = IsEndpointNearCorrectionBoundary(p0);
                    var p1CorrectionAdjacent = IsEndpointNearCorrectionBoundary(p1);

                    scannedEndpoints++;
                    if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else if (p0CorrectionAdjacent && TryFindCorrectionAdjacentSnapTarget(p0, p1, out var snappedStart))
                    {
                        if (p0.GetDistanceTo(snappedStart) <= endpointTouchTol)
                        {
                            alreadyOnBoundary++;
                        }
                        else
                        {
                            moveStart = true;
                            targetStart = snappedStart;
                        }
                    }
                    else if (!p0CorrectionAdjacent && IsEndpointOnValidBoundary(p0))
                    {
                        alreadyOnBoundary++;
                    }
                    else if (TryFindSnapTarget(p0, p1, out snappedStart))
                    {
                        if (p0.GetDistanceTo(snappedStart) <= endpointTouchTol)
                        {
                            alreadyOnBoundary++;
                        }
                        else
                        {
                            moveStart = true;
                            targetStart = snappedStart;
                        }
                    }
                    else if (IsEndpointOnValidBoundary(p0))
                    {
                        alreadyOnBoundary++;
                    }
                    else
                    {
                        noTarget++;
                    }

                    scannedEndpoints++;
                    if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol))
                    {
                        boundarySkipped++;
                    }
                    else if (p1CorrectionAdjacent && TryFindCorrectionAdjacentSnapTarget(p1, p0, out var snappedEnd))
                    {
                        if (p1.GetDistanceTo(snappedEnd) <= endpointTouchTol)
                        {
                            alreadyOnBoundary++;
                        }
                        else
                        {
                            moveEnd = true;
                            targetEnd = snappedEnd;
                        }
                    }
                    else if (!p1CorrectionAdjacent && IsEndpointOnValidBoundary(p1))
                    {
                        alreadyOnBoundary++;
                    }
                    else if (TryFindSnapTarget(p1, p0, out snappedEnd))
                    {
                        if (p1.GetDistanceTo(snappedEnd) <= endpointTouchTol)
                        {
                            alreadyOnBoundary++;
                        }
                        else
                        {
                            moveEnd = true;
                            targetEnd = snappedEnd;
                        }
                    }
                    else if (IsEndpointOnValidBoundary(p1))
                    {
                        alreadyOnBoundary++;
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

        private static bool TryEnforceLsdLineEndpointsByRuleMatrix(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<QuarterLabelInfo>? lsdQuarterInfos,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || lsdQuarterInfos == null || lsdQuarterInfos.Count == 0)
            {
                return false;
            }

            var requestedScopeIds = requestedQuarterIds
                .Where(id => !id.IsNull)
                .Distinct()
                .ToList();
            if (requestedScopeIds.Count == 0)
            {
                return false;
            }
            var requestedScopeSet = new HashSet<ObjectId>(requestedScopeIds);

            var clipWindows = BuildBufferedQuarterWindows(database, requestedScopeIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);


            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);



            bool IsUsecZeroLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
            }

            bool IsUsecTwentyLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsBlindUsecLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsSecLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsCorrectionZeroLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
            }

            bool IsGroupASection(int sectionNumber)
            {
                return (sectionNumber >= 1 && sectionNumber <= 6) ||
                       (sectionNumber >= 13 && sectionNumber <= 18) ||
                       (sectionNumber >= 25 && sectionNumber <= 30);
            }

            bool IsWestQuarter(QuarterSelection quarter)
            {
                return quarter == QuarterSelection.SouthWest || quarter == QuarterSelection.NorthWest;
            }

            bool IsEastQuarter(QuarterSelection quarter)
            {
                return quarter == QuarterSelection.SouthEast || quarter == QuarterSelection.NorthEast;
            }

            bool IsSouthQuarter(QuarterSelection quarter)
            {
                return quarter == QuarterSelection.SouthWest || quarter == QuarterSelection.SouthEast;
            }

            bool IsNorthQuarter(QuarterSelection quarter)
            {
                return quarter == QuarterSelection.NorthWest || quarter == QuarterSelection.NorthEast;
            }

            bool TryGetInnerEndpointTarget(
                QuarterSelection quarter,
                bool lineIsHorizontal,
                Point2d topAnchor,
                Point2d bottomAnchor,
                Point2d leftAnchor,
                Point2d rightAnchor,
                out Point2d target)
            {
                target = default;
                switch (quarter)
                {
                    case QuarterSelection.SouthWest:
                        target = lineIsHorizontal ? rightAnchor : topAnchor;
                        return true;
                    case QuarterSelection.SouthEast:
                        target = lineIsHorizontal ? leftAnchor : topAnchor;
                        return true;
                    case QuarterSelection.NorthWest:
                        target = lineIsHorizontal ? rightAnchor : bottomAnchor;
                        return true;
                    case QuarterSelection.NorthEast:
                        target = lineIsHorizontal ? leftAnchor : bottomAnchor;
                        return true;
                    default:
                        return false;
                }
            }

            bool TryGetOuterEndpointTarget(
                QuarterSelection quarter,
                bool lineIsHorizontal,
                Point2d topAnchor,
                Point2d bottomAnchor,
                Point2d leftAnchor,
                Point2d rightAnchor,
                out Point2d target)
            {
                target = default;
                switch (quarter)
                {
                    case QuarterSelection.SouthWest:
                        target = lineIsHorizontal ? leftAnchor : bottomAnchor;
                        return true;
                    case QuarterSelection.SouthEast:
                        target = lineIsHorizontal ? rightAnchor : bottomAnchor;
                        return true;
                    case QuarterSelection.NorthWest:
                        target = lineIsHorizontal ? leftAnchor : topAnchor;
                        return true;
                    case QuarterSelection.NorthEast:
                        target = lineIsHorizontal ? rightAnchor : topAnchor;
                        return true;
                    default:
                        return false;
                }
            }

            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);

            double ProjectOnAxis(Point2d p, Vector2d axis)
            {
                return (p.X * axis.X) + (p.Y * axis.Y);
            }

            var horizontalByKind = new Dictionary<string, List<(Point2d A, Point2d B, Point2d Mid)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SEC"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["ZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["TWENTY"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["BLIND"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["CORRZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
            };
            var verticalByKind = new Dictionary<string, List<(Point2d A, Point2d B, Point2d Mid)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SEC"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["ZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["TWENTY"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["BLIND"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["CORRZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
            };
            var correctionBoundarySegmentsWithLayers = new List<(Point2d A, Point2d B, string Layer)>();
            var correctionZeroHorizontal = new List<(Point2d A, Point2d B, Point2d Mid)>();
            var qsecSegments = new List<(Point2d A, Point2d B)>();
            var lsdLineIds = new List<ObjectId>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var quarterContexts = new List<(
                    ObjectId QuarterId,
                    ObjectId SectionPolylineId,
                    QuarterSelection Quarter,
                    int SectionNumber,
                    Polyline QuarterPolyline,
                    Extents3d QuarterExtents,
                    Point2d SectionCenter,
                    Vector2d EastUnit,
                    Vector2d NorthUnit,
                    double SectionMinU,
                    double SectionMaxU,
                    double SectionMinV,
                    double SectionMaxV,
                    Point2d SectionOrigin,
                    double SectionWestEdgeU,
                    double SectionEastEdgeU,
                    double SectionSouthEdgeV,
                    double SectionNorthEdgeV,
                    double SectionMidU,
                    double SectionMidV,
                    Point2d SectionTopAnchor,
                    Point2d SectionBottomAnchor,
                    Point2d SectionLeftAnchor,
                    Point2d SectionRightAnchor,
                    Point2d TopAnchor,
                    Point2d BottomAnchor,
                    Point2d LeftAnchor,
                    Point2d RightAnchor)>();

                var seenQuarterIds = new HashSet<ObjectId>();
                foreach (var info in lsdQuarterInfos)
                {
                    if (info == null ||
                        info.QuarterId.IsNull ||
                        info.SectionPolylineId.IsNull ||
                        !seenQuarterIds.Add(info.QuarterId))
                    {
                        continue;
                    }

                    // Limit rule-matrix quarter ownership to requested scope only.
                    if (!requestedScopeSet.Contains(info.QuarterId) &&
                        !requestedScopeSet.Contains(info.SectionPolylineId))
                    {
                        continue;
                    }

                    if (!(tr.GetObject(info.QuarterId, OpenMode.ForRead, false) is Polyline quarter) || quarter.IsErased)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(info.SectionPolylineId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                    {
                        continue;
                    }

                    var sectionNumber = ParseSectionNumber(info.SectionKey.Section);
                    if (sectionNumber < 1 || sectionNumber > 36)
                    {
                        continue;
                    }

                    QuarterAnchors sectionAnchors;
                    if (!TryGetQuarterAnchors(section, out sectionAnchors))
                    {
                        sectionAnchors = GetFallbackAnchors(section);
                    }

                    var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1.0, 0.0));
                    var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0.0, 1.0));
                    Point2d center;
                    if (!TryIntersectInfiniteLines(sectionAnchors.Left, sectionAnchors.Right, sectionAnchors.Bottom, sectionAnchors.Top, out center))
                    {
                        center = new Point2d(
                            0.25 * (sectionAnchors.Top.X + sectionAnchors.Bottom.X + sectionAnchors.Left.X + sectionAnchors.Right.X),
                            0.25 * (sectionAnchors.Top.Y + sectionAnchors.Bottom.Y + sectionAnchors.Left.Y + sectionAnchors.Right.Y));
                    }

                    if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var sectionOrigin))
                    {
                        Extents3d sectionExtents;
                        try
                        {
                            sectionExtents = section.GeometricExtents;
                        }
                        catch
                        {
                            continue;
                        }

                        sectionOrigin = new Point2d(sectionExtents.MinPoint.X, sectionExtents.MinPoint.Y);
                    }

                    var southWestCorner = sectionOrigin;
                    var southEastCorner = sectionOrigin + (eastUnit * 1.0);
                    var northWestCorner = sectionOrigin + (northUnit * 1.0);
                    var northEastCorner = sectionOrigin + (eastUnit * 1.0) + (northUnit * 1.0);
                    var haveSectionCorners =
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthWest, out northWestCorner) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthEast, out northEastCorner) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out southWestCorner) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out southEastCorner);

                    var sectionWestEdgeU = 0.5 * (
                        ProjectPointToQuarterU(southWestCorner, sectionOrigin, eastUnit) +
                        ProjectPointToQuarterU(northWestCorner, sectionOrigin, eastUnit));
                    var sectionEastEdgeU = 0.5 * (
                        ProjectPointToQuarterU(southEastCorner, sectionOrigin, eastUnit) +
                        ProjectPointToQuarterU(northEastCorner, sectionOrigin, eastUnit));
                    var sectionSouthEdgeV = 0.5 * (
                        ProjectPointToQuarterV(southWestCorner, sectionOrigin, northUnit) +
                        ProjectPointToQuarterV(southEastCorner, sectionOrigin, northUnit));
                    var sectionNorthEdgeV = 0.5 * (
                        ProjectPointToQuarterV(northWestCorner, sectionOrigin, northUnit) +
                        ProjectPointToQuarterV(northEastCorner, sectionOrigin, northUnit));
                    if (!haveSectionCorners ||
                        sectionWestEdgeU >= sectionEastEdgeU ||
                        sectionSouthEdgeV >= sectionNorthEdgeV)
                    {
                        sectionWestEdgeU = double.MaxValue;
                        sectionEastEdgeU = double.MinValue;
                        sectionSouthEdgeV = double.MaxValue;
                        sectionNorthEdgeV = double.MinValue;
                        for (var vi = 0; vi < section.NumberOfVertices; vi++)
                        {
                            var sp = section.GetPoint2dAt(vi);
                            var rel = sp - sectionOrigin;
                            var localU = rel.DotProduct(eastUnit);
                            var localV = rel.DotProduct(northUnit);
                            if (localU < sectionWestEdgeU)
                            {
                                sectionWestEdgeU = localU;
                            }

                            if (localU > sectionEastEdgeU)
                            {
                                sectionEastEdgeU = localU;
                            }

                            if (localV < sectionSouthEdgeV)
                            {
                                sectionSouthEdgeV = localV;
                            }

                            if (localV > sectionNorthEdgeV)
                            {
                                sectionNorthEdgeV = localV;
                            }
                        }
                    }

                    if (sectionWestEdgeU >= sectionEastEdgeU || sectionSouthEdgeV >= sectionNorthEdgeV)
                    {
                        continue;
                    }

                    var sectionMidU = 0.5 * (
                        ProjectPointToQuarterU(sectionAnchors.Top, sectionOrigin, eastUnit) +
                        ProjectPointToQuarterU(sectionAnchors.Bottom, sectionOrigin, eastUnit));
                    var sectionMidV = 0.5 * (
                        ProjectPointToQuarterV(sectionAnchors.Left, sectionOrigin, northUnit) +
                        ProjectPointToQuarterV(sectionAnchors.Right, sectionOrigin, northUnit));

                    var fallbackLsdAnchors = GetLsdAnchorsForQuarter(quarter, eastUnit, northUnit);
                    var northQsecHalfMid = Midpoint(center, sectionAnchors.Top);
                    var southQsecHalfMid = Midpoint(center, sectionAnchors.Bottom);
                    var westQsecHalfMid = Midpoint(center, sectionAnchors.Left);
                    var eastQsecHalfMid = Midpoint(center, sectionAnchors.Right);
                    var sectionMinU = double.MaxValue;
                    var sectionMaxU = double.MinValue;
                    var sectionMinV = double.MaxValue;
                    var sectionMaxV = double.MinValue;
                    for (var vi = 0; vi < section.NumberOfVertices; vi++)
                    {
                        var sp = section.GetPoint2dAt(vi);
                        var u = ProjectOnAxis(sp, eastUnit);
                        var v = ProjectOnAxis(sp, northUnit);
                        if (u < sectionMinU)
                        {
                            sectionMinU = u;
                        }

                        if (u > sectionMaxU)
                        {
                            sectionMaxU = u;
                        }

                        if (v < sectionMinV)
                        {
                            sectionMinV = v;
                        }

                        if (v > sectionMaxV)
                        {
                            sectionMaxV = v;
                        }
                    }

                    if (sectionMinU >= sectionMaxU || sectionMinV >= sectionMaxV)
                    {
                        continue;
                    }

                    QuarterAnchors lsdAnchors;
                    switch (info.Quarter)
                    {
                        case QuarterSelection.SouthWest:
                            lsdAnchors = new QuarterAnchors(
                                westQsecHalfMid,
                                fallbackLsdAnchors.Bottom,
                                fallbackLsdAnchors.Left,
                                southQsecHalfMid);
                            break;
                        case QuarterSelection.SouthEast:
                            lsdAnchors = new QuarterAnchors(
                                eastQsecHalfMid,
                                fallbackLsdAnchors.Bottom,
                                southQsecHalfMid,
                                fallbackLsdAnchors.Right);
                            break;
                        case QuarterSelection.NorthWest:
                            lsdAnchors = new QuarterAnchors(
                                fallbackLsdAnchors.Top,
                                westQsecHalfMid,
                                fallbackLsdAnchors.Left,
                                northQsecHalfMid);
                            break;
                        case QuarterSelection.NorthEast:
                            lsdAnchors = new QuarterAnchors(
                                fallbackLsdAnchors.Top,
                                eastQsecHalfMid,
                                northQsecHalfMid,
                                fallbackLsdAnchors.Right);
                            break;
                        default:
                            lsdAnchors = fallbackLsdAnchors;
                            break;
                    }

                    Extents3d quarterExtents;
                    try
                    {
                        quarterExtents = quarter.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(
                            new Point2d(quarterExtents.MinPoint.X, quarterExtents.MinPoint.Y),
                            new Point2d(quarterExtents.MaxPoint.X, quarterExtents.MaxPoint.Y)))
                    {
                        continue;
                    }

                    quarterContexts.Add((
                        info.QuarterId,
                        info.SectionPolylineId,
                        info.Quarter,
                        sectionNumber,
                        quarter,
                        quarterExtents,
                        center,
                        eastUnit,
                        northUnit,
                        sectionMinU,
                        sectionMaxU,
                        sectionMinV,
                        sectionMaxV,
                        sectionOrigin,
                        sectionWestEdgeU,
                        sectionEastEdgeU,
                        sectionSouthEdgeV,
                        sectionNorthEdgeV,
                        sectionMidU,
                        sectionMidV,
                        sectionAnchors.Top,
                        sectionAnchors.Bottom,
                        sectionAnchors.Left,
                        sectionAnchors.Right,
                        lsdAnchors.Top,
                        lsdAnchors.Bottom,
                        lsdAnchors.Left,
                        lsdAnchors.Right));
                }

                if (quarterContexts.Count == 0)
                {
                    return false;
                }

                void AddBoundarySegmentByKind(string kind, Point2d a, Point2d b)
                {
                    var midpoint = Midpoint(a, b);
                    if (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y))
                    {
                        if (horizontalByKind.TryGetValue(kind, out var bucket))
                        {
                            bucket.Add((a, b, midpoint));
                        }
                    }
                    else
                    {
                        if (verticalByKind.TryGetValue(kind, out var bucket))
                        {
                            bucket.Add((a, b, midpoint));
                        }
                    }
                }

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        qsecSegments.Add((a, b));
                        continue;
                    }

                    if (string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdLineIds.Add(id);
                        }

                        continue;
                    }

                    if (IsSecLayer(layer))
                    {
                        AddBoundarySegmentByKind("SEC", a, b);
                    }

                    if (IsUsecZeroLayer(layer))
                    {
                        AddBoundarySegmentByKind("ZERO", a, b);
                    }

                    if (IsUsecTwentyLayer(layer))
                    {
                        AddBoundarySegmentByKind("TWENTY", a, b);
                    }

                    if (IsBlindUsecLayer(layer))
                    {
                        AddBoundarySegmentByKind("BLIND", a, b);
                    }

                    if (IsCorrectionZeroLayer(layer))
                    {
                        AddBoundarySegmentByKind("CORRZERO", a, b);
                        AddBoundarySegmentByKind("ZERO", a, b);
                        correctionBoundarySegmentsWithLayers.Add((a, b, layer));
                        if (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y))
                        {
                            correctionZeroHorizontal.Add((a, b, Midpoint(a, b)));
                        }
                    }

                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        correctionBoundarySegmentsWithLayers.Add((a, b, layer));
                    }

                }

                bool TryResolveQsecHalfMids(
                    Point2d provisionalCenter,
                    Vector2d eastUnit,
                    Vector2d northUnit,
                    double sectionMinU,
                    double sectionMaxU,
                    double sectionMinV,
                    double sectionMaxV,
                    out Point2d westHalfMid,
                    out Point2d eastHalfMid,
                    out Point2d southHalfMid,
                    out Point2d northHalfMid)
                {
                    westHalfMid = provisionalCenter;
                    eastHalfMid = provisionalCenter;
                    southHalfMid = provisionalCenter;
                    northHalfMid = provisionalCenter;
                    if (qsecSegments.Count == 0)
                    {
                        return false;
                    }

                    const double qsecAxisClusterTol = 2.50;
                    const double qsecMergeGapTol = 14.00;
                    const double centerOnComponentTol = 10.00;
                    const double centerSpanTol = 40.00;
                    const double sectionScopePad = 8.0;

                    var centerU = ProjectOnAxis(provisionalCenter, eastUnit);
                    var centerV = ProjectOnAxis(provisionalCenter, northUnit);
                    var scopedQsecSegments = new List<(Point2d A, Point2d B)>();
                    for (var si = 0; si < qsecSegments.Count; si++)
                    {
                        var seg = qsecSegments[si];
                        var mid = Midpoint(seg.A, seg.B);
                        var midU = ProjectOnAxis(mid, eastUnit);
                        var midV = ProjectOnAxis(mid, northUnit);
                        if (midU < (sectionMinU - sectionScopePad) || midU > (sectionMaxU + sectionScopePad) ||
                            midV < (sectionMinV - sectionScopePad) || midV > (sectionMaxV + sectionScopePad))
                        {
                            continue;
                        }

                        scopedQsecSegments.Add(seg);
                    }

                    if (scopedQsecSegments.Count == 0)
                    {
                        return false;
                    }

                    var horizontalComponents = new List<(double MinU, double MaxU, double SumV, int Count)>();
                    var verticalComponents = new List<(double MinV, double MaxV, double SumU, int Count)>();
                    for (var si = 0; si < scopedQsecSegments.Count; si++)
                    {
                        var seg = scopedQsecSegments[si];
                        var d = seg.B - seg.A;
                        var eastSpan = Math.Abs(d.DotProduct(eastUnit));
                        var northSpan = Math.Abs(d.DotProduct(northUnit));
                        var aU = ProjectOnAxis(seg.A, eastUnit);
                        var bU = ProjectOnAxis(seg.B, eastUnit);
                        var aV = ProjectOnAxis(seg.A, northUnit);
                        var bV = ProjectOnAxis(seg.B, northUnit);
                        if (eastSpan >= northSpan)
                        {
                            var axisV = 0.5 * (aV + bV);
                            var minU = Math.Min(aU, bU);
                            var maxU = Math.Max(aU, bU);
                            var merged = false;
                            for (var ci = 0; ci < horizontalComponents.Count; ci++)
                            {
                                var c = horizontalComponents[ci];
                                var cAxisV = c.SumV / Math.Max(1, c.Count);
                                if (Math.Abs(axisV - cAxisV) > qsecAxisClusterTol)
                                {
                                    continue;
                                }

                                if (maxU < (c.MinU - qsecMergeGapTol) || minU > (c.MaxU + qsecMergeGapTol))
                                {
                                    continue;
                                }

                                c.MinU = Math.Min(c.MinU, minU);
                                c.MaxU = Math.Max(c.MaxU, maxU);
                                c.SumV += axisV;
                                c.Count += 1;
                                horizontalComponents[ci] = c;
                                merged = true;
                                break;
                            }

                            if (!merged)
                            {
                                horizontalComponents.Add((minU, maxU, axisV, 1));
                            }
                        }
                        else
                        {
                            var axisU = 0.5 * (aU + bU);
                            var minV = Math.Min(aV, bV);
                            var maxV = Math.Max(aV, bV);
                            var merged = false;
                            for (var ci = 0; ci < verticalComponents.Count; ci++)
                            {
                                var c = verticalComponents[ci];
                                var cAxisU = c.SumU / Math.Max(1, c.Count);
                                if (Math.Abs(axisU - cAxisU) > qsecAxisClusterTol)
                                {
                                    continue;
                                }

                                if (maxV < (c.MinV - qsecMergeGapTol) || minV > (c.MaxV + qsecMergeGapTol))
                                {
                                    continue;
                                }

                                c.MinV = Math.Min(c.MinV, minV);
                                c.MaxV = Math.Max(c.MaxV, maxV);
                                c.SumU += axisU;
                                c.Count += 1;
                                verticalComponents[ci] = c;
                                merged = true;
                                break;
                            }

                            if (!merged)
                            {
                                verticalComponents.Add((minV, maxV, axisU, 1));
                            }
                        }
                    }

                    if (horizontalComponents.Count == 0 || verticalComponents.Count == 0)
                    {
                        return false;
                    }

                    var foundH = false;
                    var bestH = default((double MinU, double MaxU, double SumV, int Count));
                    var bestHAxisGap = double.MaxValue;
                    for (var i = 0; i < horizontalComponents.Count; i++)
                    {
                        var c = horizontalComponents[i];
                        var axisV = c.SumV / Math.Max(1, c.Count);
                        var axisGap = Math.Abs(axisV - centerV);
                        if (axisGap > centerOnComponentTol)
                        {
                            continue;
                        }

                        if (centerU < (c.MinU - centerSpanTol) || centerU > (c.MaxU + centerSpanTol))
                        {
                            continue;
                        }

                        if (!foundH || axisGap < bestHAxisGap)
                        {
                            foundH = true;
                            bestH = c;
                            bestHAxisGap = axisGap;
                        }
                    }

                    var foundV = false;
                    var bestV = default((double MinV, double MaxV, double SumU, int Count));
                    var bestVAxisGap = double.MaxValue;
                    for (var i = 0; i < verticalComponents.Count; i++)
                    {
                        var c = verticalComponents[i];
                        var axisU = c.SumU / Math.Max(1, c.Count);
                        var axisGap = Math.Abs(axisU - centerU);
                        if (axisGap > centerOnComponentTol)
                        {
                            continue;
                        }

                        if (centerV < (c.MinV - centerSpanTol) || centerV > (c.MaxV + centerSpanTol))
                        {
                            continue;
                        }

                        if (!foundV || axisGap < bestVAxisGap)
                        {
                            foundV = true;
                            bestV = c;
                            bestVAxisGap = axisGap;
                        }
                    }

                    if (!foundH || !foundV)
                    {
                        return false;
                    }

                    var hAxisV = bestH.SumV / Math.Max(1, bestH.Count);
                    var vAxisU = bestV.SumU / Math.Max(1, bestV.Count);
                    var qsecCenter = provisionalCenter +
                        (eastUnit * (vAxisU - centerU)) +
                        (northUnit * (hAxisV - centerV));

                    var westEndpoint = qsecCenter + (eastUnit * (bestH.MinU - vAxisU));
                    var eastEndpoint = qsecCenter + (eastUnit * (bestH.MaxU - vAxisU));
                    var southEndpoint = qsecCenter + (northUnit * (bestV.MinV - hAxisV));
                    var northEndpoint = qsecCenter + (northUnit * (bestV.MaxV - hAxisV));

                    westHalfMid = Midpoint(qsecCenter, westEndpoint);
                    eastHalfMid = Midpoint(qsecCenter, eastEndpoint);
                    southHalfMid = Midpoint(qsecCenter, southEndpoint);
                    northHalfMid = Midpoint(qsecCenter, northEndpoint);
                    return true;
                }

                var qsecAnchorOverrides = 0;
                for (var qi = 0; qi < quarterContexts.Count; qi++)
                {
                    var context = quarterContexts[qi];
                    if (!TryResolveQsecHalfMids(
                            context.SectionCenter,
                            context.EastUnit,
                            context.NorthUnit,
                            context.SectionMinU,
                            context.SectionMaxU,
                            context.SectionMinV,
                            context.SectionMaxV,
                            out var westQsecHalfMid,
                            out var eastQsecHalfMid,
                            out var southQsecHalfMid,
                            out var northQsecHalfMid))
                    {
                        continue;
                    }

                    var topAnchor = context.TopAnchor;
                    var bottomAnchor = context.BottomAnchor;
                    var leftAnchor = context.LeftAnchor;
                    var rightAnchor = context.RightAnchor;
                    switch (context.Quarter)
                    {
                        case QuarterSelection.SouthWest:
                            topAnchor = westQsecHalfMid;
                            rightAnchor = southQsecHalfMid;
                            break;
                        case QuarterSelection.SouthEast:
                            topAnchor = eastQsecHalfMid;
                            leftAnchor = southQsecHalfMid;
                            break;
                        case QuarterSelection.NorthWest:
                            bottomAnchor = westQsecHalfMid;
                            rightAnchor = northQsecHalfMid;
                            break;
                        case QuarterSelection.NorthEast:
                            bottomAnchor = eastQsecHalfMid;
                            leftAnchor = northQsecHalfMid;
                            break;
                    }

                    quarterContexts[qi] = (
                        context.QuarterId,
                        context.SectionPolylineId,
                        context.Quarter,
                        context.SectionNumber,
                        context.QuarterPolyline,
                        context.QuarterExtents,
                        context.SectionCenter,
                        context.EastUnit,
                        context.NorthUnit,
                        context.SectionMinU,
                        context.SectionMaxU,
                        context.SectionMinV,
                        context.SectionMaxV,
                        context.SectionOrigin,
                        context.SectionWestEdgeU,
                        context.SectionEastEdgeU,
                        context.SectionSouthEdgeV,
                        context.SectionNorthEdgeV,
                        context.SectionMidU,
                        context.SectionMidV,
                        context.SectionTopAnchor,
                        context.SectionBottomAnchor,
                        context.SectionLeftAnchor,
                        context.SectionRightAnchor,
                        topAnchor,
                        bottomAnchor,
                        leftAnchor,
                        rightAnchor);
                    qsecAnchorOverrides++;
                }

                if (lsdLineIds.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine("Cleanup: LSD rule-matrix pass skipped (no L-SECTION-LSD lines in scope).");
                    return true;
                }

                bool IsPointNearAnySegment(Point2d p, List<(Point2d A, Point2d B, Point2d Mid)> segments, double tol)
                {
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        if (DistancePointToSegment(p, seg.A, seg.B) <= tol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryResolveQuarterCorrectionSouthBoundary(
                    (ObjectId QuarterId,
                        ObjectId SectionPolylineId,
                        QuarterSelection Quarter,
                        int SectionNumber,
                        Polyline QuarterPolyline,
                        Extents3d QuarterExtents,
                        Point2d SectionCenter,
                        Vector2d EastUnit,
                        Vector2d NorthUnit,
                        double SectionMinU,
                        double SectionMaxU,
                        double SectionMinV,
                        double SectionMaxV,
                        Point2d SectionOrigin,
                        double SectionWestEdgeU,
                        double SectionEastEdgeU,
                        double SectionSouthEdgeV,
                        double SectionNorthEdgeV,
                        double SectionMidU,
                        double SectionMidV,
                        Point2d SectionTopAnchor,
                        Point2d SectionBottomAnchor,
                        Point2d SectionLeftAnchor,
                        Point2d SectionRightAnchor,
                        Point2d TopAnchor,
                        Point2d BottomAnchor,
                        Point2d LeftAnchor,
                        Point2d RightAnchor) context,
                    out QuarterViewSectionFrame frame,
                    out Point2d correctionSouthA,
                    out Point2d correctionSouthB,
                    out double correctionSouthMinU,
                    out double correctionSouthMaxU)
                {
                    frame = default;
                    correctionSouthA = default;
                    correctionSouthB = default;
                    correctionSouthMinU = default;
                    correctionSouthMaxU = default;
                    if (!IsSouthQuarter(context.Quarter) || correctionBoundarySegmentsWithLayers.Count == 0)
                    {
                        return false;
                    }

                    frame = new QuarterViewSectionFrame(
                        context.SectionPolylineId,
                        context.SectionNumber,
                        context.SectionOrigin,
                        context.EastUnit,
                        context.NorthUnit,
                        context.SectionWestEdgeU,
                        context.SectionEastEdgeU,
                        context.SectionSouthEdgeV,
                        context.SectionNorthEdgeV,
                        context.SectionMidU,
                        context.SectionMidV,
                        context.SectionTopAnchor,
                        context.SectionRightAnchor,
                        context.SectionBottomAnchor,
                        context.SectionLeftAnchor,
                        context.QuarterExtents);

                    var dividerLineA = context.SectionBottomAnchor;
                    var dividerLineB = context.SectionTopAnchor;
                    if (TryResolveQsecHalfMids(
                            context.SectionCenter,
                            context.EastUnit,
                            context.NorthUnit,
                            context.SectionMinU,
                            context.SectionMaxU,
                            context.SectionMinV,
                            context.SectionMaxV,
                            out _,
                            out _,
                            out var southHalfMid,
                            out var northHalfMid))
                    {
                        dividerLineA = southHalfMid;
                        dividerLineB = northHalfMid;
                    }

                    var preferredDividerU = frame.MidU;
                    if (TryIntersectLocalInfiniteLines(
                            frame,
                            dividerLineA,
                            dividerLineB,
                            context.SectionLeftAnchor,
                            context.SectionRightAnchor,
                            out var dividerAxisU,
                            out _))
                    {
                        preferredDividerU = dividerAxisU;
                    }
                    else if (TryConvertQuarterWorldToLocal(frame, dividerLineA, out var dividerAu, out _) &&
                             TryConvertQuarterWorldToLocal(frame, dividerLineB, out var dividerBu, out _))
                    {
                        preferredDividerU = 0.5 * (dividerAu + dividerBu);
                    }

                    if (!TryResolveQuarterViewSouthCorrectionBoundaryV(
                            frame,
                            correctionBoundarySegmentsWithLayers,
                            preferredDividerU,
                            dividerLineA,
                            dividerLineB,
                            out _,
                            out correctionSouthA,
                            out correctionSouthB))
                    {
                        return false;
                    }

                    if (!TryConvertQuarterWorldToLocal(frame, correctionSouthA, out var southAu, out _) ||
                        !TryConvertQuarterWorldToLocal(frame, correctionSouthB, out var southBu, out _))
                    {
                        return false;
                    }

                    correctionSouthMinU = Math.Min(southAu, southBu);
                    correctionSouthMaxU = Math.Max(southAu, southBu);
                    return true;
                }

                bool IsQuarterCorrectionSouthStationCompatible(
                    Point2d stationReference,
                    (ObjectId QuarterId,
                        ObjectId SectionPolylineId,
                        QuarterSelection Quarter,
                        int SectionNumber,
                        Polyline QuarterPolyline,
                        Extents3d QuarterExtents,
                        Point2d SectionCenter,
                        Vector2d EastUnit,
                        Vector2d NorthUnit,
                        double SectionMinU,
                        double SectionMaxU,
                        double SectionMinV,
                        double SectionMaxV,
                        Point2d SectionOrigin,
                        double SectionWestEdgeU,
                        double SectionEastEdgeU,
                        double SectionSouthEdgeV,
                        double SectionNorthEdgeV,
                        double SectionMidU,
                        double SectionMidV,
                        Point2d SectionTopAnchor,
                        Point2d SectionBottomAnchor,
                        Point2d SectionLeftAnchor,
                        Point2d SectionRightAnchor,
                        Point2d TopAnchor,
                        Point2d BottomAnchor,
                        Point2d LeftAnchor,
                        Point2d RightAnchor) context)
                {
                    const double resolvedSpanPad = 25.0;
                    if (!TryResolveQuarterCorrectionSouthBoundary(
                            context,
                            out var frame,
                            out _,
                            out _,
                            out var correctionSouthMinU,
                            out var correctionSouthMaxU))
                    {
                        return true;
                    }

                    if (!TryConvertQuarterWorldToLocal(frame, stationReference, out var stationU, out _))
                    {
                        return true;
                    }

                    return BoundaryStationSpanPolicy.IsWithinSegmentSpan(
                        stationU,
                        correctionSouthMinU,
                        correctionSouthMaxU,
                        resolvedSpanPad);
                }

                bool TryFindQuarterInteriorCorrectionZeroTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    (ObjectId QuarterId,
                        ObjectId SectionPolylineId,
                        QuarterSelection Quarter,
                        int SectionNumber,
                        Polyline QuarterPolyline,
                        Extents3d QuarterExtents,
                        Point2d SectionCenter,
                        Vector2d EastUnit,
                        Vector2d NorthUnit,
                        double SectionMinU,
                        double SectionMaxU,
                        double SectionMinV,
                        double SectionMaxV,
                        Point2d SectionOrigin,
                        double SectionWestEdgeU,
                        double SectionEastEdgeU,
                        double SectionSouthEdgeV,
                        double SectionNorthEdgeV,
                        double SectionMidU,
                        double SectionMidV,
                        Point2d SectionTopAnchor,
                        Point2d SectionBottomAnchor,
                        Point2d SectionLeftAnchor,
                        Point2d SectionRightAnchor,
                        Point2d TopAnchor,
                        Point2d BottomAnchor,
                        Point2d LeftAnchor,
                        Point2d RightAnchor) context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (correctionZeroHorizontal.Count == 0)
                    {
                        return false;
                    }

                    var outward = endpoint - innerEndpoint;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var stationReference = innerEndpoint;
                    var boundaryPoint = IsSouthQuarter(context.Quarter)
                        ? context.BottomAnchor
                        : context.TopAnchor;
                    var boundaryV = ProjectOnAxis(boundaryPoint, context.NorthUnit);
                    var expectedInteriorSign = IsSouthQuarter(context.Quarter) ? 1.0 : -1.0;
                    const double sectionScopePad = 40.0;
                    const double stationTol = 8.0;
                    const double minOutwardAdvance = 2.0;
                    const double boundarySideTol = 0.75;

                    var foundInset = false;
                    var bestInsetTarget = endpoint;
                    var bestInsetTargetError = double.MaxValue;
                    var bestInsetBoundaryGap = double.MaxValue;
                    var bestInsetMove = double.MaxValue;
                    var foundFallback = false;
                    var bestFallbackTarget = endpoint;
                    var bestFallbackBoundaryGap = double.MaxValue;
                    var bestFallbackMove = double.MaxValue;
                    for (var i = 0; i < correctionZeroHorizontal.Count; i++)
                    {
                        var seg = correctionZeroHorizontal[i];
                        if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                stationReference,
                                seg.A,
                                seg.B,
                                context.EastUnit,
                                stationTol,
                                out var targetPoint))
                        {
                            continue;
                        }

                        var targetU = ProjectOnAxis(targetPoint, context.EastUnit);
                        var targetV = ProjectOnAxis(targetPoint, context.NorthUnit);
                        if (targetU < (context.SectionMinU - sectionScopePad) || targetU > (context.SectionMaxU + sectionScopePad) ||
                            targetV < (context.SectionMinV - sectionScopePad) || targetV > (context.SectionMaxV + sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (targetPoint - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var signedBoundaryGap = targetV - boundaryV;
                        if ((expectedInteriorSign > 0.0 && signedBoundaryGap < -boundarySideTol) ||
                            (expectedInteriorSign < 0.0 && signedBoundaryGap > boundarySideTol))
                        {
                            continue;
                        }

                        var boundaryGap = Math.Abs(signedBoundaryGap);
                        var move = endpoint.GetDistanceTo(targetPoint);
                        var prefersInset =
                            CorrectionSouthBoundaryPreference.IsCloserToInsetThanHardBoundary(
                                boundaryGap,
                                CorrectionLineInsetMeters,
                                RoadAllowanceSecWidthMeters) &&
                            CorrectionSouthBoundaryPreference.IsPlausibleInsetOffset(
                                boundaryGap,
                                CorrectionLineInsetMeters);
                        if (prefersInset)
                        {
                            var targetError = Math.Abs(boundaryGap - CorrectionLineInsetMeters);
                            if (!foundInset ||
                                CorrectionZeroTargetPreference.IsBetterInsetCandidate(
                                    targetError,
                                    boundaryGap,
                                    move,
                                    bestInsetTargetError,
                                    bestInsetBoundaryGap,
                                    bestInsetMove))
                            {
                                foundInset = true;
                                bestInsetTarget = targetPoint;
                                bestInsetTargetError = targetError;
                                bestInsetBoundaryGap = boundaryGap;
                                bestInsetMove = move;
                            }

                            continue;
                        }

                        // If no true inset companion survives, fall back to the nearest valid
                        // correction-zero row rather than forcing a synthetic target.
                        if (!foundFallback ||
                            CorrectionZeroTargetPreference.IsBetterCandidate(
                                move,
                                boundaryGap,
                                bestFallbackMove,
                                bestFallbackBoundaryGap))
                        {
                            foundFallback = true;
                            bestFallbackBoundaryGap = boundaryGap;
                            bestFallbackMove = move;
                            bestFallbackTarget = targetPoint;
                        }
                    }

                    if (foundInset)
                    {
                        target = bestInsetTarget;
                        return true;
                    }

                    if (foundFallback)
                    {
                        target = bestFallbackTarget;
                        return true;
                    }

                    return false;
                }

                bool TryFindQuarterResolvedCorrectionZeroTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    (ObjectId QuarterId,
                        ObjectId SectionPolylineId,
                        QuarterSelection Quarter,
                        int SectionNumber,
                        Polyline QuarterPolyline,
                        Extents3d QuarterExtents,
                        Point2d SectionCenter,
                        Vector2d EastUnit,
                        Vector2d NorthUnit,
                        double SectionMinU,
                        double SectionMaxU,
                        double SectionMinV,
                        double SectionMaxV,
                        Point2d SectionOrigin,
                        double SectionWestEdgeU,
                        double SectionEastEdgeU,
                        double SectionSouthEdgeV,
                        double SectionNorthEdgeV,
                        double SectionMidU,
                        double SectionMidV,
                        Point2d SectionTopAnchor,
                        Point2d SectionBottomAnchor,
                        Point2d SectionLeftAnchor,
                        Point2d SectionRightAnchor,
                        Point2d TopAnchor,
                        Point2d BottomAnchor,
                        Point2d LeftAnchor,
                        Point2d RightAnchor) context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!IsSouthQuarter(context.Quarter) || correctionBoundarySegmentsWithLayers.Count == 0)
                    {
                        return false;
                    }

                    bool TryProjectCorrectionBoundaryVAcrossSection(
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

                        const double sectionPad = 20.0;
                        if (targetU < (frame.WestEdgeU - sectionPad) || targetU > (frame.EastEdgeU + sectionPad))
                        {
                            return false;
                        }

                        var t = (targetU - uA) / du;
                        projectedV = vA + ((vB - vA) * t);
                        return true;
                    }

                    if (!TryResolveQuarterCorrectionSouthBoundary(
                            context,
                            out var frame,
                            out var correctionSouthA,
                            out var correctionSouthB,
                            out var correctionSouthMinU,
                            out var correctionSouthMaxU))
                    {
                        return false;
                    }

                    if (!TryConvertQuarterWorldToLocal(frame, innerEndpoint, out var endpointU, out _))
                    {
                        return false;
                    }

                    const double resolvedSpanPad = 25.0;
                    if (!BoundaryStationSpanPolicy.IsWithinSegmentSpan(
                            endpointU,
                            correctionSouthMinU,
                            correctionSouthMaxU,
                            resolvedSpanPad))
                    {
                        return false;
                    }

                    if (!TryProjectCorrectionBoundaryVAcrossSection(
                            frame,
                            correctionSouthA,
                            correctionSouthB,
                            endpointU,
                            out var projectedV))
                    {
                        return false;
                    }

                    var outwardDistance = frame.SouthEdgeV - projectedV;
                    if (!CorrectionSouthBoundaryPreference.IsCloserToInsetThanHardBoundary(
                            outwardDistance,
                            CorrectionLineInsetMeters,
                            RoadAllowanceSecWidthMeters) ||
                        !CorrectionSouthBoundaryPreference.IsPlausibleInsetOffset(
                            outwardDistance,
                            CorrectionLineInsetMeters) ||
                        Math.Abs(outwardDistance - CorrectionLineInsetMeters) > 6.0)
                    {
                        return false;
                    }

                    var targetPoint = new Point2d(
                        frame.Origin.X + (frame.EastUnit.X * endpointU) + (frame.NorthUnit.X * projectedV),
                        frame.Origin.Y + (frame.EastUnit.Y * endpointU) + (frame.NorthUnit.Y * projectedV));
                    var outward = endpoint - innerEndpoint;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    if ((targetPoint - innerEndpoint).DotProduct(outwardDir) < 2.0)
                    {
                        return false;
                    }

                    target = targetPoint;
                    return true;
                }

                bool TrySnapCorrectionZeroTargetToLiveSegment(
                    Point2d resolvedTarget,
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    (ObjectId QuarterId,
                        ObjectId SectionPolylineId,
                        QuarterSelection Quarter,
                        int SectionNumber,
                        Polyline QuarterPolyline,
                        Extents3d QuarterExtents,
                        Point2d SectionCenter,
                        Vector2d EastUnit,
                        Vector2d NorthUnit,
                        double SectionMinU,
                        double SectionMaxU,
                        double SectionMinV,
                        double SectionMaxV,
                        Point2d SectionOrigin,
                        double SectionWestEdgeU,
                        double SectionEastEdgeU,
                        double SectionSouthEdgeV,
                        double SectionNorthEdgeV,
                        double SectionMidU,
                        double SectionMidV,
                        Point2d SectionTopAnchor,
                        Point2d SectionBottomAnchor,
                        Point2d SectionLeftAnchor,
                        Point2d SectionRightAnchor,
                        Point2d TopAnchor,
                        Point2d BottomAnchor,
                        Point2d LeftAnchor,
                        Point2d RightAnchor) context,
                    out Point2d target)
                {
                    target = resolvedTarget;
                    if (correctionZeroHorizontal.Count == 0)
                    {
                        return false;
                    }

                    var outward = endpoint - innerEndpoint;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var boundaryPoint = IsSouthQuarter(context.Quarter)
                        ? context.BottomAnchor
                        : context.TopAnchor;
                    var boundaryV = ProjectOnAxis(boundaryPoint, context.NorthUnit);
                    var expectedInteriorSign = IsSouthQuarter(context.Quarter) ? 1.0 : -1.0;
                    const double sectionScopePad = 40.0;
                    const double stationTol = 12.0;
                    const double maxResolvedDelta = 0.25;
                    const double minOutwardAdvance = 2.0;
                    const double boundarySideTol = 0.75;
                    var stationReference = innerEndpoint;

                    var found = false;
                    var bestTarget = resolvedTarget;
                    var bestTargetDelta = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < correctionZeroHorizontal.Count; i++)
                    {
                        var seg = correctionZeroHorizontal[i];
                        if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                stationReference,
                                seg.A,
                                seg.B,
                                context.EastUnit,
                                stationTol,
                                out var candidate))
                        {
                            continue;
                        }

                        var targetU = ProjectOnAxis(candidate, context.EastUnit);
                        var targetV = ProjectOnAxis(candidate, context.NorthUnit);
                        if (targetU < (context.SectionMinU - sectionScopePad) || targetU > (context.SectionMaxU + sectionScopePad) ||
                            targetV < (context.SectionMinV - sectionScopePad) || targetV > (context.SectionMaxV + sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (candidate - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var signedBoundaryGap = targetV - boundaryV;
                        if ((expectedInteriorSign > 0.0 && signedBoundaryGap < -boundarySideTol) ||
                            (expectedInteriorSign < 0.0 && signedBoundaryGap > boundarySideTol))
                        {
                            continue;
                        }

                        var targetDelta = resolvedTarget.GetDistanceTo(candidate);
                        if (targetDelta > maxResolvedDelta)
                        {
                            continue;
                        }

                        var move = endpoint.GetDistanceTo(candidate);
                        if (!found ||
                            CorrectionZeroTargetPreference.IsBetterLiveSnapCandidate(
                                targetDelta,
                                move,
                                bestTargetDelta,
                                bestMove))
                        {
                            found = true;
                            bestTarget = candidate;
                            bestTargetDelta = targetDelta;
                            bestMove = move;
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = bestTarget;
                    return true;
                }

                bool TryFindQuarterContext(
                    Point2d point,
                    out (ObjectId QuarterId,
                        ObjectId SectionPolylineId,
                        QuarterSelection Quarter,
                        int SectionNumber,
                        Polyline QuarterPolyline,
                        Extents3d QuarterExtents,
                        Point2d SectionCenter,
                        Vector2d EastUnit,
                        Vector2d NorthUnit,
                        double SectionMinU,
                        double SectionMaxU,
                        double SectionMinV,
                        double SectionMaxV,
                        Point2d SectionOrigin,
                        double SectionWestEdgeU,
                        double SectionEastEdgeU,
                        double SectionSouthEdgeV,
                        double SectionNorthEdgeV,
                        double SectionMidU,
                        double SectionMidV,
                        Point2d SectionTopAnchor,
                        Point2d SectionBottomAnchor,
                        Point2d SectionLeftAnchor,
                        Point2d SectionRightAnchor,
                        Point2d TopAnchor,
                        Point2d BottomAnchor,
                        Point2d LeftAnchor,
                        Point2d RightAnchor) context)
                {
                    context = default;
                    var found = false;
                    var bestScore = double.MaxValue;
                    const double extPad = 2.5;
                    for (var i = 0; i < quarterContexts.Count; i++)
                    {
                        var candidate = quarterContexts[i];
                        var ext = candidate.QuarterExtents;
                        if (point.X < (ext.MinPoint.X - extPad) || point.X > (ext.MaxPoint.X + extPad) ||
                            point.Y < (ext.MinPoint.Y - extPad) || point.Y > (ext.MaxPoint.Y + extPad))
                        {
                            continue;
                        }

                        if (!GeometryUtils.IsPointInsidePolyline(candidate.QuarterPolyline, point))
                        {
                            continue;
                        }

                        var score = point.GetDistanceTo(candidate.SectionCenter);
                        if (score >= bestScore)
                        {
                            continue;
                        }

                        bestScore = score;
                        context = candidate;
                        found = true;
                    }

                    return found;
                }

                bool TryFindBoundaryStationTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    bool lineIsHorizontal,
                    Vector2d eastUnit,
                    Vector2d northUnit,
                    double sectionMinU,
                    double sectionMaxU,
                    double sectionMinV,
                    double sectionMaxV,
                    IReadOnlyList<string> preferredKinds,
                    out Point2d target)
                {
                    target = endpoint;
                    if (preferredKinds == null || preferredKinds.Count == 0)
                    {
                        return false;
                    }

                    var source = lineIsHorizontal ? verticalByKind : horizontalByKind;
                    var outward = endpoint - innerEndpoint;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var stationAxis = lineIsHorizontal ? northUnit : eastUnit;
                    const double minMove = 0.005;
                    const double maxMove = 140.0;
                    const double axisTol = 20.0;
                    const double stationTol = 8.0;
                    const double minOutwardAdvance = 2.0;
                    // RA boundaries (0/20.12 and correction variants) can sit ~20-30m outside
                    // section extents; a tight pad causes south/west LSD outer endpoints to miss
                    // valid TWENTY/ZERO targets and fall back to anchors.
                    var sectionScopePad = 8.0;
                    for (var pi = 0; pi < preferredKinds.Count; pi++)
                    {
                        var kind = preferredKinds[pi];
                        if (!string.Equals(kind, "SEC", StringComparison.OrdinalIgnoreCase))
                        {
                            sectionScopePad = 40.0;
                            break;
                        }
                    }
                    const double endpointOnBoundaryTol = 0.40;
                    var found = false;
                    var bestScore = double.MaxValue;

                    bool IsPointNearAnyHardBoundary(Point2d p)
                    {
                        bool NearAny(IReadOnlyList<(Point2d A, Point2d B, Point2d Mid)> segments)
                        {
                            if (segments == null || segments.Count == 0)
                            {
                                return false;
                            }

                            for (var i = 0; i < segments.Count; i++)
                            {
                                var seg = segments[i];
                                if (DistancePointToSegment(p, seg.A, seg.B) <= endpointOnBoundaryTol)
                                {
                                    return true;
                                }
                            }

                            return false;
                        }

                        return
                            (horizontalByKind.TryGetValue("SEC", out var hSec) && NearAny(hSec)) ||
                            (horizontalByKind.TryGetValue("ZERO", out var hZero) && NearAny(hZero)) ||
                            (horizontalByKind.TryGetValue("TWENTY", out var hTwenty) && NearAny(hTwenty)) ||
                            (horizontalByKind.TryGetValue("CORRZERO", out var hCorrZero) && NearAny(hCorrZero)) ||
                            (verticalByKind.TryGetValue("SEC", out var vSec) && NearAny(vSec)) ||
                            (verticalByKind.TryGetValue("ZERO", out var vZero) && NearAny(vZero)) ||
                            (verticalByKind.TryGetValue("TWENTY", out var vTwenty) && NearAny(vTwenty)) ||
                            (verticalByKind.TryGetValue("CORRZERO", out var vCorrZero) && NearAny(vCorrZero));
                    }

                    int CountHardEndpointTouches((Point2d A, Point2d B, Point2d Mid) seg)
                    {
                        var touches = 0;
                        if (IsPointNearAnyHardBoundary(seg.A))
                        {
                            touches++;
                        }

                        if (IsPointNearAnyHardBoundary(seg.B))
                        {
                            touches++;
                        }

                        return touches;
                    }

                    // If endpoint already lands on the primary preferred boundary, mark it as
                    // preservable but keep scanning midpoint candidates first. This avoids
                    // freezing on stale pre-adjust rows when a post-adjust candidate exists.
                    var preserveOnPrimaryBoundary = false;
                    for (var pi = 0; pi < preferredKinds.Count; pi++)
                    {
                        var kind = preferredKinds[pi];
                        if (!source.TryGetValue(kind, out var segments) || segments.Count == 0)
                        {
                            continue;
                        }

                        for (var si = 0; si < segments.Count; si++)
                        {
                            var seg = segments[si];
                            if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                    endpoint,
                                    seg.A,
                                    seg.B,
                                    stationAxis,
                                    stationTol,
                                    out var preservePoint))
                            {
                                continue;
                            }

                            var preserveU = ProjectOnAxis(preservePoint, eastUnit);
                            var preserveV = ProjectOnAxis(preservePoint, northUnit);
                            if (preserveU < (sectionMinU - sectionScopePad) || preserveU > (sectionMaxU + sectionScopePad) ||
                                preserveV < (sectionMinV - sectionScopePad) || preserveV > (sectionMaxV + sectionScopePad))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(endpoint, seg.A, seg.B) > endpointOnBoundaryTol)
                            {
                                continue;
                            }

                            double endpointStation;
                            double aStation;
                            double bStation;
                            if (lineIsHorizontal)
                            {
                                endpointStation = ProjectOnAxis(endpoint, stationAxis);
                                aStation = ProjectOnAxis(seg.A, stationAxis);
                                bStation = ProjectOnAxis(seg.B, stationAxis);
                            }
                            else
                            {
                                endpointStation = ProjectOnAxis(endpoint, stationAxis);
                                aStation = ProjectOnAxis(seg.A, stationAxis);
                                bStation = ProjectOnAxis(seg.B, stationAxis);
                            }

                            var minStation = Math.Min(aStation, bStation) - stationTol;
                            var maxStation = Math.Max(aStation, bStation) + stationTol;
                            if (endpointStation < minStation || endpointStation > maxStation)
                            {
                                continue;
                            }

                            if (pi > 0)
                            {
                                // Do not freeze endpoint on a lower-priority fallback boundary
                                // (e.g., SEC) when higher-priority targets (e.g., TWENTY/ZERO)
                                // are configured for this quarter/line orientation.
                                continue;
                            }

                            // Blind boundaries can exist in parallel pre/post-adjustment states.
                            // Only freeze when the candidate is strongly hard-linked at both ends;
                            // otherwise continue scoring to allow snap onto the post-adjusted blind midpoint.
                            if (string.Equals(kind, "BLIND", StringComparison.OrdinalIgnoreCase))
                            {
                                var hardTouches = CountHardEndpointTouches(seg);
                                if (hardTouches < 2)
                                {
                                    continue;
                                }
                            }

                            // CORRZERO rows can exist in multiple parallel seam bands across repeated
                            // cleanup passes. Once the endpoint already lands on the primary correction
                            // band, keep it there instead of walking outward to another parallel row.
                            if (CorrectionZeroTargetPreference.ShouldPreserveExistingPrimaryBoundary(kind))
                            {
                                target = endpoint;
                                return true;
                            }

                            preserveOnPrimaryBoundary = true;
                            continue;
                        }
                    }

                    for (var pi = 0; pi < preferredKinds.Count; pi++)
                    {
                        var kind = preferredKinds[pi];
                        if (!source.TryGetValue(kind, out var segments) || segments.Count == 0)
                        {
                            continue;
                        }

                        for (var si = 0; si < segments.Count; si++)
                        {
                            var seg = segments[si];
                            if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                    endpoint,
                                    seg.A,
                                    seg.B,
                                    stationAxis,
                                    stationTol,
                                    out var targetPoint))
                            {
                                continue;
                            }

                            var targetU = ProjectOnAxis(targetPoint, eastUnit);
                            var targetV = ProjectOnAxis(targetPoint, northUnit);
                            if (targetU < (sectionMinU - sectionScopePad) || targetU > (sectionMaxU + sectionScopePad) ||
                                targetV < (sectionMinV - sectionScopePad) || targetV > (sectionMaxV + sectionScopePad))
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(targetPoint);
                            if (move <= minMove || move > maxMove)
                            {
                                continue;
                            }

                            double endpointStation;
                            double aStation;
                            double bStation;
                            double axisGap;
                            if (lineIsHorizontal)
                            {
                                endpointStation = ProjectOnAxis(endpoint, stationAxis);
                                aStation = ProjectOnAxis(seg.A, stationAxis);
                                bStation = ProjectOnAxis(seg.B, stationAxis);
                                axisGap = Math.Abs(ProjectOnAxis(targetPoint, stationAxis) - endpointStation);
                            }
                            else
                            {
                                endpointStation = ProjectOnAxis(endpoint, stationAxis);
                                aStation = ProjectOnAxis(seg.A, stationAxis);
                                bStation = ProjectOnAxis(seg.B, stationAxis);
                                axisGap = Math.Abs(ProjectOnAxis(targetPoint, stationAxis) - endpointStation);
                            }

                            if (axisGap > axisTol)
                            {
                                continue;
                            }

                            var minStation = Math.Min(aStation, bStation) - stationTol;
                            var maxStation = Math.Max(aStation, bStation) + stationTol;
                            if (endpointStation < minStation || endpointStation > maxStation)
                            {
                                continue;
                            }

                            var outwardAdvance = (targetPoint - innerEndpoint).DotProduct(outwardDir);
                            if (outwardAdvance < minOutwardAdvance)
                            {
                                continue;
                            }

                            // Prefer the first valid boundary encountered outward from the inner target.
                            // This prevents endpoint scoring from skipping a nearer SEC boundary and crossing to a farther one.
                            // For BLIND candidates, strongly prefer boundaries whose endpoints tie into
                            // hard section boundaries (SEC/0/20/CORRZERO). This avoids locking to
                            // pre-adjust blind geometry when a post-adjust segment is present.
                            var hardLinkPenalty = 0.0;
                            if (string.Equals(kind, "BLIND", StringComparison.OrdinalIgnoreCase))
                            {
                                var hardTouches = CountHardEndpointTouches(seg);
                                hardLinkPenalty = (2 - hardTouches) * 10000.0;
                            }

                            var score = (pi * 1000000.0) + hardLinkPenalty + (outwardAdvance * 1000.0) + (axisGap * 100.0) + move;
                            if (score >= bestScore)
                            {
                                continue;
                            }

                            bestScore = score;
                            target = targetPoint;
                            found = true;
                        }
                    }

                    if (found)
                    {
                        return true;
                    }

                    if (preserveOnPrimaryBoundary)
                    {
                        target = endpoint;
                        return true;
                    }

                    return false;
                }

                const double endpointMoveTol = 0.005;
                var traceRuleFlow = logger != null;

                if (traceRuleFlow)
                {
                    logger?.WriteLine(
                        $"LSD-ENDPT rule-matrix init quarters={quarterContexts.Count} lsdLines={lsdLineIds.Count} qsecSegs={qsecSegments.Count} " +
                        $"secH={horizontalByKind["SEC"].Count} secV={verticalByKind["SEC"].Count} " +
                        $"zeroH={horizontalByKind["ZERO"].Count} zeroV={verticalByKind["ZERO"].Count} " +
                        $"twentyH={horizontalByKind["TWENTY"].Count} twentyV={verticalByKind["TWENTY"].Count} " +
                        $"blindH={horizontalByKind["BLIND"].Count} blindV={verticalByKind["BLIND"].Count} " +
                        $"corrZeroH={horizontalByKind["CORRZERO"].Count} corrZeroV={verticalByKind["CORRZERO"].Count}.");
                }

                var matchedLines = 0;
                var noQuarterContext = 0;
                var noInnerTarget = 0;
                var noOuterTarget = 0;
                var innerAdjusted = 0;
                var outerAdjusted = 0;
                var correctionZeroOverrides = 0;
                for (var i = 0; i < lsdLineIds.Count; i++)
                {
                    var id = lsdLineIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var lineIdText = FormatLsdEndpointTraceId(id);
                    if (traceRuleFlow)
                    {
                        logger?.WriteLine(
                            $"LSD-ENDPT line={lineIdText} pass=rule-matrix start p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)}.");
                    }

                    var lineMid = Midpoint(p0, p1);
                    if (!TryFindQuarterContext(lineMid, out var context))
                    {
                        noQuarterContext++;
                        if (traceRuleFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix skip reason=no-quarter-context mid={FormatLsdEndpointTracePoint(lineMid)}.");
                        }

                        continue;
                    }

                    var lineIsHorizontal = IsHorizontalLikeForEndpointEnforcement(p0, p1, context.EastUnit, context.NorthUnit);
                    var lineIsVertical = IsVerticalLikeForEndpointEnforcement(p0, p1, context.EastUnit, context.NorthUnit);
                    if (!lineIsHorizontal && !lineIsVertical)
                    {
                        if (traceRuleFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix skip reason=non-axis p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)}.");
                        }

                        continue;
                    }

                    matchedLines++;
                    var hasAnchorInnerTarget = TryGetInnerEndpointTarget(
                        context.Quarter,
                        lineIsHorizontal,
                        context.TopAnchor,
                        context.BottomAnchor,
                        context.LeftAnchor,
                        context.RightAnchor,
                        out var anchorInnerTarget);

                    bool TryResolveRuleMatrixLiveInnerTarget(out Point2d target)
                    {
                        target = default;
                        if (qsecSegments.Count == 0)
                        {
                            return false;
                        }

                        const double sectionScopePad = 8.0;
                        const double axisTol = 16.0;
                        const double spanTol = 40.0;
                        const double sideWindow = 2.75;
                        const double sideAdvanceTol = 2.0;
                        if (lineIsHorizontal)
                        {
                            var p0U = ProjectOnAxis(p0, context.EastUnit);
                            var p1U = ProjectOnAxis(p1, context.EastUnit);
                            var useP0 = IsWestQuarter(context.Quarter)
                                ? p0U >= p1U
                                : p0U <= p1U;
                            var endpoint = useP0 ? p0 : p1;
                            var other = useP0 ? p1 : p0;
                            var referencePoint = hasAnchorInnerTarget ? anchorInnerTarget : endpoint;
                            var endpointU = ProjectOnAxis(referencePoint, context.EastUnit);
                            var otherU = ProjectOnAxis(other, context.EastUnit);
                            var targetV = ProjectOnAxis(referencePoint, context.NorthUnit);
                            var candidates = new List<(Point2d Point, double U, double AxisGap, double SpanGap)>();
                            for (var qsecIndex = 0; qsecIndex < qsecSegments.Count; qsecIndex++)
                            {
                                var seg = qsecSegments[qsecIndex];
                                var mid = Midpoint(seg.A, seg.B);
                                var midU = ProjectOnAxis(mid, context.EastUnit);
                                var midV = ProjectOnAxis(mid, context.NorthUnit);
                                if (midU < (context.SectionMinU - sectionScopePad) || midU > (context.SectionMaxU + sectionScopePad) ||
                                    midV < (context.SectionMinV - sectionScopePad) || midV > (context.SectionMaxV + sectionScopePad))
                                {
                                    continue;
                                }

                                var d = seg.B - seg.A;
                                var eastSpan = Math.Abs(d.DotProduct(context.EastUnit));
                                var northSpan = Math.Abs(d.DotProduct(context.NorthUnit));
                                if (northSpan <= eastSpan)
                                {
                                    continue;
                                }

                                var aV = ProjectOnAxis(seg.A, context.NorthUnit);
                                var bV = ProjectOnAxis(seg.B, context.NorthUnit);
                                var minV = Math.Min(aV, bV);
                                var maxV = Math.Max(aV, bV);
                                if (targetV < (minV - spanTol) || targetV > (maxV + spanTol))
                                {
                                    continue;
                                }

                                var spanGap = 0.0;
                                if (targetV < minV)
                                {
                                    spanGap = minV - targetV;
                                }
                                else if (targetV > maxV)
                                {
                                    spanGap = targetV - maxV;
                                }

                                var t = 0.5;
                                var dv = bV - aV;
                                if (Math.Abs(dv) > 1e-6)
                                {
                                    t = (targetV - aV) / dv;
                                }

                                if (t < 0.0) t = 0.0;
                                if (t > 1.0) t = 1.0;
                                var point = seg.A + ((seg.B - seg.A) * t);
                                var candidateU = ProjectOnAxis(point, context.EastUnit);
                                var candidateAxisGap = Math.Abs(endpointU - candidateU);
                                if (candidateAxisGap > axisTol)
                                {
                                    continue;
                                }

                                candidates.Add((point, candidateU, candidateAxisGap, spanGap));
                            }

                            if (candidates.Count == 0)
                            {
                                return false;
                            }

                            var minAxisGap = double.MaxValue;
                            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                            {
                                var candidateAxisGap = candidates[candidateIndex].AxisGap;
                                if (candidateAxisGap < minAxisGap)
                                {
                                    minAxisGap = candidateAxisGap;
                                }
                            }

                            var axisCutoff = minAxisGap + sideWindow;
                            var preferLowerU = endpointU <= otherU;
                            bool IsSideCandidate((Point2d Point, double U, double AxisGap, double SpanGap) c)
                            {
                                return preferLowerU
                                    ? c.U <= (otherU - sideAdvanceTol)
                                    : c.U >= (otherU + sideAdvanceTol);
                            }

                            var hasSideCandidate = false;
                            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                            {
                                var candidate = candidates[candidateIndex];
                                if (candidate.AxisGap > axisCutoff || !IsSideCandidate(candidate))
                                {
                                    continue;
                                }

                                hasSideCandidate = true;
                                break;
                            }

                            var found = false;
                            var bestPoint = endpoint;
                            var bestU = endpointU;
                            var bestAxisGap = double.MaxValue;
                            var bestSpanGap = double.MaxValue;
                            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                            {
                                var candidate = candidates[candidateIndex];
                                if (candidate.AxisGap > axisCutoff)
                                {
                                    continue;
                                }

                                if (hasSideCandidate && !IsSideCandidate(candidate))
                                {
                                    continue;
                                }

                                var better = !found;
                                if (!better)
                                {
                                    if (preferLowerU)
                                    {
                                        if (candidate.U < (bestU - 1e-6))
                                        {
                                            better = true;
                                        }
                                        else if (Math.Abs(candidate.U - bestU) <= 1e-6)
                                        {
                                            better =
                                                candidate.AxisGap < (bestAxisGap - 1e-6) ||
                                                (Math.Abs(candidate.AxisGap - bestAxisGap) <= 1e-6 && candidate.SpanGap < bestSpanGap);
                                        }
                                    }
                                    else
                                    {
                                        if (candidate.U > (bestU + 1e-6))
                                        {
                                            better = true;
                                        }
                                        else if (Math.Abs(candidate.U - bestU) <= 1e-6)
                                        {
                                            better =
                                                candidate.AxisGap < (bestAxisGap - 1e-6) ||
                                                (Math.Abs(candidate.AxisGap - bestAxisGap) <= 1e-6 && candidate.SpanGap < bestSpanGap);
                                        }
                                    }
                                }

                                if (!better)
                                {
                                    continue;
                                }

                                found = true;
                                bestPoint = candidate.Point;
                                bestU = candidate.U;
                                bestAxisGap = candidate.AxisGap;
                                bestSpanGap = candidate.SpanGap;
                            }

                            if (found)
                            {
                                target = bestPoint;
                                return true;
                            }

                            return false;
                        }

                        var p0V = ProjectOnAxis(p0, context.NorthUnit);
                        var p1V = ProjectOnAxis(p1, context.NorthUnit);
                        var useVerticalP0 = IsSouthQuarter(context.Quarter)
                            ? p0V >= p1V
                            : p0V <= p1V;
                        var verticalEndpoint = useVerticalP0 ? p0 : p1;
                        var verticalOther = useVerticalP0 ? p1 : p0;
                        var verticalReferencePoint = hasAnchorInnerTarget ? anchorInnerTarget : verticalEndpoint;
                        var endpointV = ProjectOnAxis(verticalReferencePoint, context.NorthUnit);
                        var otherV = ProjectOnAxis(verticalOther, context.NorthUnit);
                        var targetU = ProjectOnAxis(verticalReferencePoint, context.EastUnit);
                        var verticalCandidates = new List<(Point2d Point, double V, double AxisGap, double SpanGap)>();
                        for (var qsecIndex = 0; qsecIndex < qsecSegments.Count; qsecIndex++)
                        {
                            var seg = qsecSegments[qsecIndex];
                            var mid = Midpoint(seg.A, seg.B);
                            var midU = ProjectOnAxis(mid, context.EastUnit);
                            var midV = ProjectOnAxis(mid, context.NorthUnit);
                            if (midU < (context.SectionMinU - sectionScopePad) || midU > (context.SectionMaxU + sectionScopePad) ||
                                midV < (context.SectionMinV - sectionScopePad) || midV > (context.SectionMaxV + sectionScopePad))
                            {
                                continue;
                            }

                            var d = seg.B - seg.A;
                            var eastSpan = Math.Abs(d.DotProduct(context.EastUnit));
                            var northSpan = Math.Abs(d.DotProduct(context.NorthUnit));
                            if (eastSpan < northSpan)
                            {
                                continue;
                            }

                            var aU = ProjectOnAxis(seg.A, context.EastUnit);
                            var bU = ProjectOnAxis(seg.B, context.EastUnit);
                            var minU = Math.Min(aU, bU);
                            var maxU = Math.Max(aU, bU);
                            if (targetU < (minU - spanTol) || targetU > (maxU + spanTol))
                            {
                                continue;
                            }

                            var spanGap = 0.0;
                            if (targetU < minU)
                            {
                                spanGap = minU - targetU;
                            }
                            else if (targetU > maxU)
                            {
                                spanGap = targetU - maxU;
                            }

                            var t = 0.5;
                            var du = bU - aU;
                            if (Math.Abs(du) > 1e-6)
                            {
                                t = (targetU - aU) / du;
                            }

                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            var point = seg.A + ((seg.B - seg.A) * t);
                            var candidateV = ProjectOnAxis(point, context.NorthUnit);
                            var candidateAxisGap = Math.Abs(endpointV - candidateV);
                            if (candidateAxisGap > axisTol)
                            {
                                continue;
                            }

                            verticalCandidates.Add((point, candidateV, candidateAxisGap, spanGap));
                        }

                        if (verticalCandidates.Count == 0)
                        {
                            return false;
                        }

                        var minVerticalAxisGap = double.MaxValue;
                        for (var candidateIndex = 0; candidateIndex < verticalCandidates.Count; candidateIndex++)
                        {
                            var candidateAxisGap = verticalCandidates[candidateIndex].AxisGap;
                            if (candidateAxisGap < minVerticalAxisGap)
                            {
                                minVerticalAxisGap = candidateAxisGap;
                            }
                        }

                        var verticalAxisCutoff = minVerticalAxisGap + sideWindow;
                        var preferLowerV = endpointV <= otherV;
                        bool IsVerticalSideCandidate((Point2d Point, double V, double AxisGap, double SpanGap) c)
                        {
                            return preferLowerV
                                ? c.V <= (otherV - sideAdvanceTol)
                                : c.V >= (otherV + sideAdvanceTol);
                        }

                        var hasVerticalSideCandidate = false;
                        for (var candidateIndex = 0; candidateIndex < verticalCandidates.Count; candidateIndex++)
                        {
                            var candidate = verticalCandidates[candidateIndex];
                            if (candidate.AxisGap > verticalAxisCutoff || !IsVerticalSideCandidate(candidate))
                            {
                                continue;
                            }

                            hasVerticalSideCandidate = true;
                            break;
                        }

                        var foundVertical = false;
                        var bestVerticalPoint = verticalEndpoint;
                        var bestV = endpointV;
                        var bestVerticalAxisGap = double.MaxValue;
                        var bestVerticalSpanGap = double.MaxValue;
                        for (var candidateIndex = 0; candidateIndex < verticalCandidates.Count; candidateIndex++)
                        {
                            var candidate = verticalCandidates[candidateIndex];
                            if (candidate.AxisGap > verticalAxisCutoff)
                            {
                                continue;
                            }

                            if (hasVerticalSideCandidate && !IsVerticalSideCandidate(candidate))
                            {
                                continue;
                            }

                            var better = !foundVertical;
                            if (!better)
                            {
                                if (preferLowerV)
                                {
                                    if (candidate.V < (bestV - 1e-6))
                                    {
                                        better = true;
                                    }
                                    else if (Math.Abs(candidate.V - bestV) <= 1e-6)
                                    {
                                        better =
                                            candidate.AxisGap < (bestVerticalAxisGap - 1e-6) ||
                                            (Math.Abs(candidate.AxisGap - bestVerticalAxisGap) <= 1e-6 && candidate.SpanGap < bestVerticalSpanGap);
                                    }
                                }
                                else
                                {
                                    if (candidate.V > (bestV + 1e-6))
                                    {
                                        better = true;
                                    }
                                    else if (Math.Abs(candidate.V - bestV) <= 1e-6)
                                    {
                                        better =
                                            candidate.AxisGap < (bestVerticalAxisGap - 1e-6) ||
                                            (Math.Abs(candidate.AxisGap - bestVerticalAxisGap) <= 1e-6 && candidate.SpanGap < bestVerticalSpanGap);
                                    }
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            foundVertical = true;
                            bestVerticalPoint = candidate.Point;
                            bestV = candidate.V;
                            bestVerticalAxisGap = candidate.AxisGap;
                            bestVerticalSpanGap = candidate.SpanGap;
                        }

                        if (foundVertical)
                        {
                            target = bestVerticalPoint;
                            return true;
                        }

                        return false;
                    }

                    Point2d innerTarget;
                    var innerTargetSource = "anchor";
                    if (TryResolveRuleMatrixLiveInnerTarget(out var liveInnerTarget))
                    {
                        innerTarget = liveInnerTarget;
                        innerTargetSource = "live-qsec";
                    }
                    else if (!hasAnchorInnerTarget)
                    {
                        noInnerTarget++;
                        if (traceRuleFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix skip reason=no-inner-target sec={context.SectionNumber} q={context.Quarter} orient={(lineIsHorizontal ? "H" : "V")}.");
                        }

                        continue;
                    }
                    else
                    {
                        innerTarget = anchorInnerTarget;
                    }

                    var startIsInner = p0.GetDistanceTo(innerTarget) <= p1.GetDistanceTo(innerTarget);
                    var innerMoved = TryMoveEndpoint(writable, startIsInner, innerTarget, endpointMoveTol);
                    if (innerMoved)
                    {
                        innerAdjusted++;
                    }

                    if (traceRuleFlow)
                    {
                        logger?.WriteLine(
                            $"LSD-ENDPT line={lineIdText} pass=rule-matrix inner sec={context.SectionNumber} q={context.Quarter} orient={(lineIsHorizontal ? "H" : "V")} " +
                            $"startIsInner={startIsInner} moved={innerMoved} target={FormatLsdEndpointTracePoint(innerTarget)} source={innerTargetSource}.");
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1))
                    {
                        if (traceRuleFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix skip reason=unreadable-after-inner.");
                        }

                        continue;
                    }

                    startIsInner = p0.GetDistanceTo(innerTarget) <= p1.GetDistanceTo(innerTarget);
                    var innerPoint = startIsInner ? p0 : p1;
                    var outerPoint = startIsInner ? p1 : p0;
                    var preferredKinds = new List<string>();
                    var fallbackPreferredKinds = new List<string>();

                    var correctionOverride = false;
                    if (lineIsHorizontal)
                    {
                        if (IsWestQuarter(context.Quarter))
                        {
                            fallbackPreferredKinds.Add("TWENTY");
                            fallbackPreferredKinds.Add("SEC");
                        }
                        else if (IsEastQuarter(context.Quarter))
                        {
                            fallbackPreferredKinds.Add("ZERO");
                            fallbackPreferredKinds.Add("SEC");
                        }
                    }
                    else
                    {
                        correctionOverride = IsPointNearAnySegment(outerPoint, correctionZeroHorizontal, 60.0);
                        if (IsSouthQuarter(context.Quarter))
                        {
                            if (IsGroupASection(context.SectionNumber))
                            {
                                fallbackPreferredKinds.Add("TWENTY");
                                fallbackPreferredKinds.Add("SEC");
                            }
                            else
                            {
                                fallbackPreferredKinds.Add("BLIND");
                                fallbackPreferredKinds.Add("SEC");
                            }
                        }
                        else if (IsNorthQuarter(context.Quarter))
                        {
                            if (IsGroupASection(context.SectionNumber))
                            {
                                fallbackPreferredKinds.Add("BLIND");
                                fallbackPreferredKinds.Add("SEC");
                            }
                            else
                            {
                                fallbackPreferredKinds.Add("ZERO");
                                fallbackPreferredKinds.Add("SEC");
                            }
                        }
                    }

                    preferredKinds.AddRange(fallbackPreferredKinds);
                    if (!lineIsHorizontal && correctionOverride)
                    {
                        preferredKinds.Clear();
                        preferredKinds.Add("CORRZERO");
                        preferredKinds.Add("SEC");
                        correctionZeroOverrides++;
                    }

                    if (preferredKinds.Count == 0)
                    {
                        noOuterTarget++;
                        if (traceRuleFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix skip reason=no-preferred-kinds sec={context.SectionNumber} q={context.Quarter} orient={(lineIsHorizontal ? "H" : "V")}.");
                        }

                        continue;
                    }

                    var movedByStationTarget = false;
                    var movedByFallbackAnchor = false;
                    var usedStationTarget = false;
                    var usedResolvedCorrectionZeroTarget = false;
                    var usedInteriorCorrectionZeroTarget = false;
                    var outerTarget = outerPoint;
                    var foundOuterTarget = false;
                    foundOuterTarget = TryFindBoundaryStationTarget(
                        outerPoint,
                        innerPoint,
                        lineIsHorizontal,
                        context.EastUnit,
                        context.NorthUnit,
                        context.SectionMinU,
                        context.SectionMaxU,
                        context.SectionMinV,
                        context.SectionMaxV,
                        preferredKinds,
                        out outerTarget);
                    usedStationTarget = foundOuterTarget;

                    var useQuarterCorrectionZeroTarget =
                        !foundOuterTarget &&
                        !lineIsHorizontal &&
                        correctionOverride &&
                        IsQuarterCorrectionSouthStationCompatible(innerPoint, context);
                    if (useQuarterCorrectionZeroTarget)
                    {
                        foundOuterTarget = TryFindQuarterResolvedCorrectionZeroTarget(
                            outerPoint,
                            innerPoint,
                            context,
                            out outerTarget);
                        usedResolvedCorrectionZeroTarget = foundOuterTarget;
                        if (!foundOuterTarget)
                        {
                            foundOuterTarget = TryFindQuarterInteriorCorrectionZeroTarget(
                                outerPoint,
                                innerPoint,
                                context,
                                out outerTarget);
                            usedInteriorCorrectionZeroTarget = foundOuterTarget;
                        }

                        if (foundOuterTarget)
                        {
                            if (TrySnapCorrectionZeroTargetToLiveSegment(
                                    outerTarget,
                                    outerPoint,
                                    innerPoint,
                                    context,
                                    out var snappedOuterTarget))
                            {
                                outerTarget = snappedOuterTarget;
                            }

                            usedStationTarget = true;
                        }
                    }

                    if (!foundOuterTarget)
                    {
                        if (!lineIsHorizontal && correctionOverride)
                        {
                            preferredKinds.Clear();
                            preferredKinds.AddRange(fallbackPreferredKinds);
                            if (traceRuleFlow)
                            {
                                logger?.WriteLine(
                                    $"LSD-ENDPT line={lineIdText} pass=rule-matrix correction-override-downgraded sec={context.SectionNumber} q={context.Quarter}.");
                            }
                        }

                        foundOuterTarget = TryFindBoundaryStationTarget(
                            outerPoint,
                            innerPoint,
                            lineIsHorizontal,
                            context.EastUnit,
                            context.NorthUnit,
                            context.SectionMinU,
                            context.SectionMaxU,
                            context.SectionMinV,
                            context.SectionMaxV,
                            preferredKinds,
                            out outerTarget);
                        usedStationTarget = foundOuterTarget;
                    }

                    if (!foundOuterTarget)
                    {
                        if (!TryGetOuterEndpointTarget(
                                context.Quarter,
                                lineIsHorizontal,
                                context.TopAnchor,
                                context.BottomAnchor,
                                context.LeftAnchor,
                                context.RightAnchor,
                                out outerTarget))
                        {
                            noOuterTarget++;
                            if (traceRuleFlow)
                            {
                                logger?.WriteLine(
                                    $"LSD-ENDPT line={lineIdText} pass=rule-matrix skip reason=no-outer-target sec={context.SectionNumber} q={context.Quarter} orient={(lineIsHorizontal ? "H" : "V")} kinds={string.Join("/", preferredKinds)}.");
                            }

                            continue;
                        }

                        movedByFallbackAnchor = TryMoveEndpoint(writable, !startIsInner, outerTarget, endpointMoveTol);
                        if (movedByFallbackAnchor)
                        {
                            outerAdjusted++;
                        }

                        if (traceRuleFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix outer source=fallback-anchor moved={movedByFallbackAnchor} target={FormatLsdEndpointTracePoint(outerTarget)} kinds={string.Join("/", preferredKinds)}.");
                        }

                        if (traceRuleFlow && TryReadOpenSegmentForEndpointEnforcement(writable, out var fallbackP0, out var fallbackP1))
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix final p0={FormatLsdEndpointTracePoint(fallbackP0)} p1={FormatLsdEndpointTracePoint(fallbackP1)}.");
                        }

                        continue;
                    }

                    usedStationTarget = true;
                    movedByStationTarget = TryMoveEndpoint(writable, !startIsInner, outerTarget, endpointMoveTol);
                        if (movedByStationTarget)
                        {
                            outerAdjusted++;
                            if ((context.SectionNumber >= 1 && context.SectionNumber <= 6) || context.SectionNumber == 36)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-LSD-OUTER sec={context.SectionNumber} q={context.Quarter} line={(lineIsHorizontal ? "H" : "V")} " +
                                $"inner={innerPoint.X:0.###},{innerPoint.Y:0.###} outerFrom={outerPoint.X:0.###},{outerPoint.Y:0.###} " +
                                $"outerTo={outerTarget.X:0.###},{outerTarget.Y:0.###} kinds={string.Join("/", preferredKinds)}");
                        }
                    }

                    if (traceRuleFlow)
                    {
                        var outerSource =
                            usedResolvedCorrectionZeroTarget ? "corrzero-resolved" :
                            usedInteriorCorrectionZeroTarget ? "corrzero-interior" :
                            usedStationTarget ? "station-kind" :
                            "unknown";
                        logger?.WriteLine(
                            $"LSD-ENDPT line={lineIdText} pass=rule-matrix outer source={outerSource} moved={movedByStationTarget} target={FormatLsdEndpointTracePoint(outerTarget)} kinds={string.Join("/", preferredKinds)}.");
                    }

                    if (traceRuleFlow && TryReadOpenSegmentForEndpointEnforcement(writable, out var finalP0, out var finalP1))
                    {
                        logger?.WriteLine(
                            $"LSD-ENDPT line={lineIdText} pass=rule-matrix final p0={FormatLsdEndpointTracePoint(finalP0)} p1={FormatLsdEndpointTracePoint(finalP1)}.");
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: LSD rule-matrix pass quarters={quarterContexts.Count}, lsdLines={lsdLineIds.Count}, matched={matchedLines}, qsecAnchorOverrides={qsecAnchorOverrides}, innerAdjusted={innerAdjusted}, outerAdjusted={outerAdjusted}, correctionZeroOverrides={correctionZeroOverrides}, noQuarter={noQuarterContext}, noInnerTarget={noInnerTarget}, noOuterTarget={noOuterTarget}.");
                return true;
            }
        }

        private static void EnforceLsdLineEndpointsOnHardSectionBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger,
            IReadOnlyCollection<QuarterLabelInfo>? lsdQuarterInfos = null)
        {
            if (TryEnforceLsdLineEndpointsByRuleMatrix(database, requestedQuarterIds, lsdQuarterInfos, logger))
            {
                return;
            }

            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
            }
            var coreClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 0.0);
            if (coreClipWindows.Count == 0)
            {
                coreClipWindows = clipWindows;
            }

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForEndpointEnforcement(p, tol, clipWindows);

            bool IsPointInAnyCoreWindow(Point2d p)
            {
                for (var i = 0; i < coreClipWindows.Count; i++)
                {
                    var w = coreClipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);

            bool DoesSegmentIntersectAnyCoreWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyCoreWindow(a) || IsPointInAnyCoreWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < coreClipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, coreClipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }


            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

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
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase);
            }

            bool IsThirtyEighteenLayer(string layer) => IsThirtyEighteenLayerForEndpointEnforcement(layer);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var hardBoundarySegments = new List<(Point2d A, Point2d B, bool IsZero)>();
                var correctionBoundarySegments = new List<(Point2d A, Point2d B)>();
                var correctionOuterBoundarySegments = new List<(Point2d A, Point2d B)>();
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

                    var hasPrimarySegment = TryReadOpenSegmentForEndpointEnforcement(ent, out var primaryA, out var primaryB);
                    var entitySegments = new List<(Point2d A, Point2d B)>();
                    if (ent is Line line)
                    {
                        var la = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                        var lb = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                        if (la.GetDistanceTo(lb) > 1e-4)
                        {
                            entitySegments.Add((la, lb));
                        }
                    }
                    else if (ent is Polyline polyline && polyline.NumberOfVertices >= 2)
                    {
                        for (var vi = 0; vi < polyline.NumberOfVertices - 1; vi++)
                        {
                            var sa = polyline.GetPoint2dAt(vi);
                            var sb = polyline.GetPoint2dAt(vi + 1);
                            if (sa.GetDistanceTo(sb) <= 1e-4)
                            {
                                continue;
                            }

                            entitySegments.Add((sa, sb));
                        }

                        if (polyline.Closed)
                        {
                            var sa = polyline.GetPoint2dAt(polyline.NumberOfVertices - 1);
                            var sb = polyline.GetPoint2dAt(0);
                            if (sa.GetDistanceTo(sb) > 1e-4)
                            {
                                entitySegments.Add((sa, sb));
                            }
                        }
                    }

                    if (entitySegments.Count == 0 && hasPrimarySegment)
                    {
                        entitySegments.Add((primaryA, primaryB));
                    }

                    if (entitySegments.Count == 0)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var isCorrectionLayer =
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
                    var scopedSegments = new List<(Point2d A, Point2d B)>(entitySegments.Count);
                    for (var si = 0; si < entitySegments.Count; si++)
                    {
                        var seg = entitySegments[si];
                        // Keep correction boundaries available even if they sit just outside the
                        // clipped request window; LSD correction snap can require adjacent seam rows.
                        if (isCorrectionLayer || DoesSegmentIntersectAnyWindow(seg.A, seg.B))
                        {
                            scopedSegments.Add(seg);
                        }
                    }

                    if (scopedSegments.Count == 0 && !isCorrectionLayer)
                    {
                        continue;
                    }

                    if (scopedSegments.Count == 0)
                    {
                        scopedSegments.AddRange(entitySegments);
                    }

                    for (var si = 0; si < scopedSegments.Count; si++)
                    {
                        var a = scopedSegments[si].A;
                        var b = scopedSegments[si].B;

                        if (IsUsecZeroBoundaryLayer(layer))
                        {
                            hardBoundarySegments.Add((a, b, IsZero: true));
                            if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                            {
                                correctionBoundarySegments.Add((a, b));
                            }

                            if (IsHorizontalLikeForEndpointEnforcement(a, b))
                            {
                                horizontalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 2));
                            }
                            else if (IsVerticalLikeForEndpointEnforcement(a, b))
                            {
                                verticalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 2));
                            }

                            continue;
                        }

                        if (IsUsecTwentyBoundaryLayer(layer))
                        {
                            hardBoundarySegments.Add((a, b, IsZero: false));
                            if (IsHorizontalLikeForEndpointEnforcement(a, b))
                            {
                                horizontalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 1));
                            }
                            else if (IsVerticalLikeForEndpointEnforcement(a, b))
                            {
                                verticalMidpointTargetSegments.Add((a, b, Midpoint(a, b), Priority: 1));
                            }

                            continue;
                        }

                        if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        {
                            correctionOuterBoundarySegments.Add((a, b));
                            continue;
                        }

                        if (IsThirtyEighteenLayer(layer))
                        {
                            thirtyBoundarySegments.Add((a, b));
                            continue;
                        }

                        // Midpoint targets for LSD endpoints:
                        // 1) quarter lines (preferred), 2) blind lines, 3) section lines.
                        if (IsHorizontalLikeForEndpointEnforcement(a, b))
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

                        if (IsVerticalLikeForEndpointEnforcement(a, b))
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
                    }

                    if (string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) &&
                        hasPrimarySegment &&
                        IsAdjustableLsdLineSegment(primaryA, primaryB) &&
                        DoesSegmentIntersectAnyCoreWindow(primaryA, primaryB))
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
                const double correctionAdjTol = 60.0;
                const double thirtyEscapeLateralTol = 90.0;
                const double thirtyEscapeMaxMove = 140.0;

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
                var traceLsdEndpointFlow = logger != null;



                string ClassifyOrientation(Point2d a, Point2d b)
                {
                    if (IsHorizontalLikeForEndpointEnforcement(a, b))
                    {
                        return "H";
                    }

                    if (IsVerticalLikeForEndpointEnforcement(a, b))
                    {
                        return "V";
                    }

                    return "D";
                }

                if (traceLsdEndpointFlow)
                {
                    logger?.WriteLine(
                        $"LSD-ENDPT init lsdLines={lsdLineIds.Count} hardSegs={hardBoundarySegments.Count} thirtySegs={thirtyBoundarySegments.Count} qsecH={qsecHorizontalSegments.Count} qsecV={qsecVerticalSegments.Count}.");
                }

                bool IsEndpointNearCorrectionBoundary(Point2d endpoint) =>
                    IsEndpointNearBoundarySegmentsForEndpointEnforcement(endpoint, correctionBoundarySegments, correctionAdjTol);

                bool IsEndpointOnHardBoundary(Point2d endpoint) =>
                    IsEndpointOnBoundarySegmentsForEndpointEnforcement(endpoint, hardBoundarySegments, endpointTouchTol);

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

                // Deterministic 1/4-line target:
                // midpoint between the quarter-line center intersection and the directional end
                // (W/E for horizontal QSEC, S/N for vertical QSEC).
                bool TryResolveHorizontalQsecDirectionalHalfMidpoint(
                    Point2d endpoint,
                    out double targetX,
                    out double targetY)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    if (qsecHorizontalSegments.Count == 0 || qsecVerticalSegments.Count == 0)
                    {
                        return false;
                    }

                    const double axisTol = 4.00;
                    const double horizontalSpanTol = 80.0;
                    const double verticalSpanTol = 2.50;
                    const double maxMove = 80.0;
                    var found = false;
                    var bestX = endpoint.X;
                    var bestY = endpoint.Y;
                    var bestAxisGap = double.MaxValue;
                    var bestMove = double.MaxValue;

                    for (var hi = 0; hi < qsecHorizontalSegments.Count; hi++)
                    {
                        var h = qsecHorizontalSegments[hi];
                        var minX = Math.Min(h.A.X, h.B.X);
                        var maxX = Math.Max(h.A.X, h.B.X);
                        if (endpoint.X < (minX - horizontalSpanTol) || endpoint.X > (maxX + horizontalSpanTol))
                        {
                            continue;
                        }

                        var dx = h.B.X - h.A.X;
                        var tEndpoint = 0.5;
                        if (Math.Abs(dx) > 1e-6)
                        {
                            tEndpoint = (endpoint.X - h.A.X) / dx;
                        }

                        if (tEndpoint < 0.0) tEndpoint = 0.0;
                        if (tEndpoint > 1.0) tEndpoint = 1.0;
                        var yAtEndpointX = h.A.Y + ((h.B.Y - h.A.Y) * tEndpoint);
                        var axisGap = Math.Abs(endpoint.Y - yAtEndpointX);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        var yLine = 0.5 * (h.A.Y + h.B.Y);
                        for (var vi = 0; vi < qsecVerticalSegments.Count; vi++)
                        {
                            var v = qsecVerticalSegments[vi];
                            var minY = Math.Min(v.A.Y, v.B.Y);
                            var maxY = Math.Max(v.A.Y, v.B.Y);
                            if (yLine < (minY - verticalSpanTol) || yLine > (maxY + verticalSpanTol))
                            {
                                continue;
                            }

                            var dy = v.B.Y - v.A.Y;
                            var tCenter = 0.5;
                            if (Math.Abs(dy) > 1e-6)
                            {
                                tCenter = (yLine - v.A.Y) / dy;
                            }

                            if (tCenter < 0.0) tCenter = 0.0;
                            if (tCenter > 1.0) tCenter = 1.0;
                            var xCenter = v.A.X + ((v.B.X - v.A.X) * tCenter);
                            if (xCenter < (minX - verticalSpanTol) || xCenter > (maxX + verticalSpanTol))
                            {
                                continue;
                            }

                            var candidateX = endpoint.X <= xCenter
                                ? 0.5 * (minX + xCenter)
                                : 0.5 * (maxX + xCenter);

                            var tCandidate = 0.5;
                            if (Math.Abs(dx) > 1e-6)
                            {
                                tCandidate = (candidateX - h.A.X) / dx;
                            }

                            if (tCandidate < 0.0) tCandidate = 0.0;
                            if (tCandidate > 1.0) tCandidate = 1.0;
                            var candidateY = h.A.Y + ((h.B.Y - h.A.Y) * tCandidate);
                            var move = Math.Abs(candidateX - endpoint.X);
                            if (move > maxMove)
                            {
                                continue;
                            }

                            var better =
                                !found ||
                                axisGap < (bestAxisGap - 1e-6) ||
                                (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && move < bestMove);
                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestX = candidateX;
                            bestY = candidateY;
                            bestAxisGap = axisGap;
                            bestMove = move;
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    targetX = bestX;
                    targetY = bestY;
                    return true;
                }

                bool TryResolveVerticalQsecDirectionalHalfMidpoint(
                    Point2d endpoint,
                    out double targetX,
                    out double targetY)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    if (qsecVerticalSegments.Count == 0 || qsecHorizontalSegments.Count == 0)
                    {
                        return false;
                    }

                    const double axisTol = 4.00;
                    const double verticalSpanTol = 80.0;
                    const double horizontalSpanTol = 2.50;
                    const double maxMove = 80.0;
                    var found = false;
                    var bestX = endpoint.X;
                    var bestY = endpoint.Y;
                    var bestAxisGap = double.MaxValue;
                    var bestMove = double.MaxValue;

                    for (var vi = 0; vi < qsecVerticalSegments.Count; vi++)
                    {
                        var v = qsecVerticalSegments[vi];
                        var minY = Math.Min(v.A.Y, v.B.Y);
                        var maxY = Math.Max(v.A.Y, v.B.Y);
                        if (endpoint.Y < (minY - verticalSpanTol) || endpoint.Y > (maxY + verticalSpanTol))
                        {
                            continue;
                        }

                        var dy = v.B.Y - v.A.Y;
                        var tEndpoint = 0.5;
                        if (Math.Abs(dy) > 1e-6)
                        {
                            tEndpoint = (endpoint.Y - v.A.Y) / dy;
                        }

                        if (tEndpoint < 0.0) tEndpoint = 0.0;
                        if (tEndpoint > 1.0) tEndpoint = 1.0;
                        var xAtEndpointY = v.A.X + ((v.B.X - v.A.X) * tEndpoint);
                        var axisGap = Math.Abs(endpoint.X - xAtEndpointY);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        for (var hi = 0; hi < qsecHorizontalSegments.Count; hi++)
                        {
                            var h = qsecHorizontalSegments[hi];
                            var yCenter = 0.5 * (h.A.Y + h.B.Y);
                            if (yCenter < (minY - horizontalSpanTol) || yCenter > (maxY + horizontalSpanTol))
                            {
                                continue;
                            }

                            var tCenter = 0.5;
                            if (Math.Abs(dy) > 1e-6)
                            {
                                tCenter = (yCenter - v.A.Y) / dy;
                            }

                            if (tCenter < 0.0) tCenter = 0.0;
                            if (tCenter > 1.0) tCenter = 1.0;
                            var xCenter = v.A.X + ((v.B.X - v.A.X) * tCenter);
                            var hMinX = Math.Min(h.A.X, h.B.X);
                            var hMaxX = Math.Max(h.A.X, h.B.X);
                            if (xCenter < (hMinX - horizontalSpanTol) || xCenter > (hMaxX + horizontalSpanTol))
                            {
                                continue;
                            }

                            var candidateY = endpoint.Y <= yCenter
                                ? 0.5 * (minY + yCenter)
                                : 0.5 * (maxY + yCenter);
                            var move = Math.Abs(candidateY - endpoint.Y);
                            if (move > maxMove)
                            {
                                continue;
                            }

                            var tCandidate = 0.5;
                            if (Math.Abs(dy) > 1e-6)
                            {
                                tCandidate = (candidateY - v.A.Y) / dy;
                            }

                            if (tCandidate < 0.0) tCandidate = 0.0;
                            if (tCandidate > 1.0) tCandidate = 1.0;
                            var candidateX = v.A.X + ((v.B.X - v.A.X) * tCandidate);
                            var better =
                                !found ||
                                axisGap < (bestAxisGap - 1e-6) ||
                                (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && move < bestMove);
                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestX = candidateX;
                            bestY = candidateY;
                            bestAxisGap = axisGap;
                            bestMove = move;
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    targetX = bestX;
                    targetY = bestY;
                    return true;
                }

                bool TryFindEndpointHorizontalMidpointX(Point2d endpoint, out double targetX, out double targetY, out int targetPriority)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    targetPriority = int.MaxValue;
                    if (TryResolveHorizontalQsecDirectionalHalfMidpoint(endpoint, out var directionalX, out var directionalY))
                    {
                        targetX = directionalX;
                        targetY = directionalY;
                        targetPriority = 0;
                        return true;
                    }

                    const double endpointYLineTol = 2.00;
                    const double xSpanTol = 2.00;
                    const double endpointOnSegmentTol = 0.75;
                    // Allow full-span midpoint snaps on real section/usec targets, but keep
                    // component fallback constrained to avoid long-span drift.
                    const double maxMidpointShiftForPrimarySegments = 1200.0;
                    const double maxMidpointShiftForComponentFallback = 80.0;

                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestYGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < horizontalMidpointTargetSegments.Count; i++)
                    {
                        var seg = horizontalMidpointTargetSegments[i];
                        var onSegmentTol = seg.Priority == 0
                            ? 2.00
                            : (seg.Priority == 3 ? 6.00 : endpointOnSegmentTol);
                        var yTol = seg.Priority == 0
                            ? 3.00
                            : (seg.Priority == 3 ? 4.00 : endpointYLineTol);
                        var spanTol = seg.Priority == 0
                            ? 8.00
                            : (seg.Priority == 3 ? 40.0 : xSpanTol);
                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > onSegmentTol)
                        {
                            continue;
                        }

                        var yLine = 0.5 * (seg.A.Y + seg.B.Y);
                        var yAtEndpointX = yLine;
                        var dx = seg.B.X - seg.A.X;
                        if (Math.Abs(dx) > 1e-6)
                        {
                            var t = (endpoint.X - seg.A.X) / dx;
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            yAtEndpointX = seg.A.Y + ((seg.B.Y - seg.A.Y) * t);
                        }

                        var yGap = Math.Abs(endpoint.Y - yAtEndpointX);
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
                        var maxMidpointShift = seg.Priority >= 3
                            ? maxMidpointShiftForComponentFallback
                            : maxMidpointShiftForPrimarySegments;
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

                // Exact midpoint anchor for regular horizontal boundaries (L-USEC/L-SEC/0/20).
                // This intentionally excludes quarter-line/component targets so N-S regular LSD
                // endpoints stay on the true midpoint of the boundary they already touch.
                bool TryFindEndpointRegularHorizontalBoundaryMidpoint(
                    Point2d endpoint,
                    out double targetX,
                    out double targetY,
                    out int targetPriority)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    targetPriority = int.MaxValue;
                    if (horizontalMidpointTargetSegments.Count == 0)
                    {
                        return false;
                    }

                    const double endpointYLineTol = 2.00;
                    const double xSpanTol = 2.00;
                    const double endpointOnSegmentTol = 0.75;
                    const double maxMidpointShiftForPrimarySegments = 1200.0;
                    const double maxMidpointShiftForComponentFallback = 80.0;
                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestYGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < horizontalMidpointTargetSegments.Count; i++)
                    {
                        var seg = horizontalMidpointTargetSegments[i];
                        if (seg.Priority <= 0 || seg.Priority >= 3)
                        {
                            continue;
                        }

                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > endpointOnSegmentTol)
                        {
                            continue;
                        }

                        var yLine = 0.5 * (seg.A.Y + seg.B.Y);
                        var yAtEndpointX = yLine;
                        var dx = seg.B.X - seg.A.X;
                        if (Math.Abs(dx) > 1e-6)
                        {
                            var t = (endpoint.X - seg.A.X) / dx;
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            yAtEndpointX = seg.A.Y + ((seg.B.Y - seg.A.Y) * t);
                        }

                        var yGap = Math.Abs(endpoint.Y - yAtEndpointX);
                        if (yGap > endpointYLineTol)
                        {
                            continue;
                        }

                        var minX = Math.Min(seg.A.X, seg.B.X);
                        var maxX = Math.Max(seg.A.X, seg.B.X);
                        if (endpoint.X < (minX - xSpanTol) || endpoint.X > (maxX + xSpanTol))
                        {
                            continue;
                        }

                        var move = endpoint.GetDistanceTo(seg.Mid);
                        var maxMidpointShift = seg.Priority >= 3
                            ? maxMidpointShiftForComponentFallback
                            : maxMidpointShiftForPrimarySegments;
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
                    if (TryResolveHorizontalQsecDirectionalHalfMidpoint(endpoint, out var directionalX, out var directionalY))
                    {
                        targetX = directionalX;
                        targetY = directionalY;
                        return true;
                    }

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

                bool TryFindEndpointVerticalMidpointY(Point2d endpoint, out double targetY, out int targetPriority)
                {
                    targetY = endpoint.Y;
                    targetPriority = int.MaxValue;
                    if (TryResolveVerticalQsecDirectionalHalfMidpoint(endpoint, out _, out var directionalY))
                    {
                        targetY = directionalY;
                        targetPriority = 0;
                        return true;
                    }

                    const double endpointXLineTol = 2.00;
                    const double ySpanTol = 2.00;
                    const double endpointOnSegmentTol = 0.75;
                    // Allow full-span midpoint snaps on real section/usec targets, but keep
                    // component fallback constrained to avoid long-span drift.
                    const double maxMidpointShiftForPrimarySegments = 1200.0;
                    const double maxMidpointShiftForComponentFallback = 80.0;

                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestXGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                    {
                        var seg = verticalMidpointTargetSegments[i];
                        var onSegmentTol = seg.Priority == 0
                            ? 2.00
                            : (seg.Priority == 3 ? 6.00 : endpointOnSegmentTol);
                        var xTol = seg.Priority == 0
                            ? 3.00
                            : (seg.Priority == 3 ? 4.00 : endpointXLineTol);
                        var spanTol = seg.Priority == 0
                            ? 8.00
                            : (seg.Priority == 3 ? 40.0 : ySpanTol);
                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > onSegmentTol)
                        {
                            continue;
                        }

                        var xLine = 0.5 * (seg.A.X + seg.B.X);
                        var xAtEndpointY = xLine;
                        var dy = seg.B.Y - seg.A.Y;
                        if (Math.Abs(dy) > 1e-6)
                        {
                            var t = (endpoint.Y - seg.A.Y) / dy;
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            xAtEndpointY = seg.A.X + ((seg.B.X - seg.A.X) * t);
                        }

                        var xGap = Math.Abs(endpoint.X - xAtEndpointY);
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
                        var maxMidpointShift = seg.Priority >= 3
                            ? maxMidpointShiftForComponentFallback
                            : maxMidpointShiftForPrimarySegments;
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

                // Exact midpoint anchor for regular vertical boundaries (L-USEC/L-SEC/0/20).
                // This intentionally excludes quarter-line/component targets so E-W regular LSD
                // endpoints stay on the true midpoint of the boundary they already touch.
                bool TryFindEndpointRegularVerticalBoundaryMidpoint(
                    Point2d endpoint,
                    out double targetX,
                    out double targetY,
                    out int targetPriority)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    targetPriority = int.MaxValue;
                    if (verticalMidpointTargetSegments.Count == 0)
                    {
                        return false;
                    }

                    const double endpointXLineTol = 2.00;
                    const double ySpanTol = 2.00;
                    const double endpointOnSegmentTol = 0.75;
                    const double maxMidpointShiftForPrimarySegments = 1200.0;
                    const double maxMidpointShiftForComponentFallback = 80.0;
                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestXGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                    {
                        var seg = verticalMidpointTargetSegments[i];
                        if (seg.Priority <= 0 || seg.Priority >= 3)
                        {
                            continue;
                        }

                        var onSegmentTol = endpointOnSegmentTol;
                        var xTol = endpointXLineTol;
                        var spanTol = ySpanTol;
                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > onSegmentTol)
                        {
                            continue;
                        }

                        var xLine = 0.5 * (seg.A.X + seg.B.X);
                        var xAtEndpointY = xLine;
                        var dy = seg.B.Y - seg.A.Y;
                        if (Math.Abs(dy) > 1e-6)
                        {
                            var t = (endpoint.Y - seg.A.Y) / dy;
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            xAtEndpointY = seg.A.X + ((seg.B.X - seg.A.X) * t);
                        }

                        var xGap = Math.Abs(endpoint.X - xAtEndpointY);
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

                        var move = endpoint.GetDistanceTo(seg.Mid);
                        var maxMidpointShift = seg.Priority >= 3
                            ? maxMidpointShiftForComponentFallback
                            : maxMidpointShiftForPrimarySegments;
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
                        targetX = seg.Mid.X;
                        targetY = seg.Mid.Y;
                        targetPriority = seg.Priority;
                    }

                    return found;
                }

                bool TryResolveVerticalQsecComponentMidpoint(Point2d endpoint, out double targetX, out double targetY)
                {
                    targetX = endpoint.X;
                    targetY = endpoint.Y;
                    if (TryResolveVerticalQsecDirectionalHalfMidpoint(endpoint, out var directionalX, out var directionalY))
                    {
                        targetX = directionalX;
                        targetY = directionalY;
                        return true;
                    }

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
                            var candidate = new Point2d(xLine, sideY);
                            var move = endpoint.GetDistanceTo(candidate);
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

                bool TryResolveVerticalQsecAxisX(Point2d endpoint, out double targetX)
                {
                    targetX = endpoint.X;
                    if (qsecVerticalSegments.Count == 0 && qsecVerticalComponentTargets.Count == 0)
                    {
                        return false;
                    }

                    const double axisTol = 12.00;
                    const double spanTol = 40.0;
                    var found = false;
                    var bestAxisGap = double.MaxValue;
                    var bestSpanGap = double.MaxValue;
                    var bestFromRawSegment = false;

                    // Prefer raw L-QSEC segment intersection (x at target y) so skewed vertical
                    // quarter-lines below correction lines do not drift to component-average x.
                    for (var i = 0; i < qsecVerticalSegments.Count; i++)
                    {
                        var seg = qsecVerticalSegments[i];
                        var minY = Math.Min(seg.A.Y, seg.B.Y);
                        var maxY = Math.Max(seg.A.Y, seg.B.Y);
                        if (endpoint.Y < (minY - spanTol) || endpoint.Y > (maxY + spanTol))
                        {
                            continue;
                        }

                        var spanGap = 0.0;
                        if (endpoint.Y < minY)
                        {
                            spanGap = minY - endpoint.Y;
                        }
                        else if (endpoint.Y > maxY)
                        {
                            spanGap = endpoint.Y - maxY;
                        }

                        var dy = seg.B.Y - seg.A.Y;
                        var t = 0.5;
                        if (Math.Abs(dy) > 1e-6)
                        {
                            t = (endpoint.Y - seg.A.Y) / dy;
                        }

                        if (t < 0.0) t = 0.0;
                        if (t > 1.0) t = 1.0;
                        var candidateX = seg.A.X + ((seg.B.X - seg.A.X) * t);
                        var axisGap = Math.Abs(endpoint.X - candidateX);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            !bestFromRawSegment ||
                            axisGap < (bestAxisGap - 1e-6) ||
                            (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && spanGap < bestSpanGap);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestFromRawSegment = true;
                        bestAxisGap = axisGap;
                        bestSpanGap = spanGap;
                        targetX = candidateX;
                    }

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

                        var spanGap = 0.0;
                        if (endpoint.Y < minY)
                        {
                            spanGap = minY - endpoint.Y;
                        }
                        else if (endpoint.Y > maxY)
                        {
                            spanGap = endpoint.Y - maxY;
                        }

                        var better =
                            !found ||
                            (!bestFromRawSegment &&
                             (axisGap < (bestAxisGap - 1e-6) ||
                              (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && spanGap < bestSpanGap)));
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestFromRawSegment = false;
                        bestAxisGap = axisGap;
                        bestSpanGap = spanGap;
                        targetX = xLine;
                    }

                    return found;
                }

                bool TryResolveVerticalMidpointAxisX(Point2d endpoint, double targetY, out double targetX)
                {
                    targetX = endpoint.X;
                    if (verticalMidpointTargetSegments.Count == 0)
                    {
                        return false;
                    }

                    const double baseAxisTol = 8.00;
                    const double qsecAxisTol = 12.00;
                    const double baseSpanTol = 8.00;
                    const double qsecSpanTol = 16.00;
                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestAxisGap = double.MaxValue;
                    var bestSpanGap = double.MaxValue;
                    for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                    {
                        var seg = verticalMidpointTargetSegments[i];
                        var minY = Math.Min(seg.A.Y, seg.B.Y);
                        var maxY = Math.Max(seg.A.Y, seg.B.Y);
                        var spanTol = seg.Priority == 0 ? qsecSpanTol : baseSpanTol;
                        if (targetY < (minY - spanTol) || targetY > (maxY + spanTol))
                        {
                            continue;
                        }

                        var spanGap = 0.0;
                        if (targetY < minY)
                        {
                            spanGap = minY - targetY;
                        }
                        else if (targetY > maxY)
                        {
                            spanGap = targetY - maxY;
                        }

                        var dy = seg.B.Y - seg.A.Y;
                        var t = 0.5;
                        if (Math.Abs(dy) > 1e-6)
                        {
                            t = (targetY - seg.A.Y) / dy;
                        }

                        if (t < 0.0) t = 0.0;
                        if (t > 1.0) t = 1.0;
                        var candidateX = seg.A.X + ((seg.B.X - seg.A.X) * t);
                        var axisGap = Math.Abs(endpoint.X - candidateX);
                        var axisTol = seg.Priority == 0 ? qsecAxisTol : baseAxisTol;
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            seg.Priority < bestPriority ||
                            (seg.Priority == bestPriority && axisGap < (bestAxisGap - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(axisGap - bestAxisGap) <= 1e-6 && spanGap < bestSpanGap);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestPriority = seg.Priority;
                        bestAxisGap = axisGap;
                        bestSpanGap = spanGap;
                        targetX = candidateX;
                    }

                    return found;
                }

                bool TryResolveVerticalQsecAxisXForHorizontalEndpoint(
                    Point2d endpoint,
                    Point2d other,
                    double targetY,
                    out double targetX)
                {
                    targetX = endpoint.X;
                    if (qsecVerticalSegments.Count == 0 && qsecVerticalComponentTargets.Count == 0)
                    {
                        return false;
                    }

                    const double axisTol = 16.00;
                    const double spanTol = 40.0;
                    const double sideWindow = 2.75;
                    var preferLowerX = endpoint.X <= other.X;
                    var candidates = new List<(double X, double AxisGap, double SpanGap, bool FromRaw)>();

                    for (var i = 0; i < qsecVerticalSegments.Count; i++)
                    {
                        var seg = qsecVerticalSegments[i];
                        var minY = Math.Min(seg.A.Y, seg.B.Y);
                        var maxY = Math.Max(seg.A.Y, seg.B.Y);
                        if (targetY < (minY - spanTol) || targetY > (maxY + spanTol))
                        {
                            continue;
                        }

                        var spanGap = 0.0;
                        if (targetY < minY)
                        {
                            spanGap = minY - targetY;
                        }
                        else if (targetY > maxY)
                        {
                            spanGap = targetY - maxY;
                        }

                        var dy = seg.B.Y - seg.A.Y;
                        var t = 0.5;
                        if (Math.Abs(dy) > 1e-6)
                        {
                            t = (targetY - seg.A.Y) / dy;
                        }

                        if (t < 0.0) t = 0.0;
                        if (t > 1.0) t = 1.0;
                        var x = seg.A.X + ((seg.B.X - seg.A.X) * t);
                        var axisGap = Math.Abs(endpoint.X - x);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        candidates.Add((x, axisGap, spanGap, FromRaw: true));
                    }

                    for (var i = 0; i < qsecVerticalComponentTargets.Count; i++)
                    {
                        var c = qsecVerticalComponentTargets[i];
                        var minY = Math.Min(c.A.Y, c.B.Y);
                        var maxY = Math.Max(c.A.Y, c.B.Y);
                        if (targetY < (minY - spanTol) || targetY > (maxY + spanTol))
                        {
                            continue;
                        }

                        var spanGap = 0.0;
                        if (targetY < minY)
                        {
                            spanGap = minY - targetY;
                        }
                        else if (targetY > maxY)
                        {
                            spanGap = targetY - maxY;
                        }

                        var x = 0.5 * (c.A.X + c.B.X);
                        var axisGap = Math.Abs(endpoint.X - x);
                        if (axisGap > axisTol)
                        {
                            continue;
                        }

                        candidates.Add((x, axisGap, spanGap, FromRaw: false));
                    }

                    if (candidates.Count == 0)
                    {
                        return false;
                    }

                    var minAxisGap = double.MaxValue;
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var axisGap = candidates[i].AxisGap;
                        if (axisGap < minAxisGap)
                        {
                            minAxisGap = axisGap;
                        }
                    }

                    var axisCutoff = minAxisGap + sideWindow;
                    bool IsSideCandidate((double X, double AxisGap, double SpanGap, bool FromRaw) c)
                    {
                        return preferLowerX
                            ? c.X <= (other.X - minRemainingLength)
                            : c.X >= (other.X + minRemainingLength);
                    }

                    var hasSideCandidate = false;
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var c = candidates[i];
                        if (c.AxisGap > axisCutoff)
                        {
                            continue;
                        }

                        if (!IsSideCandidate(c))
                        {
                            continue;
                        }

                        hasSideCandidate = true;
                        break;
                    }

                    var found = false;
                    var bestX = endpoint.X;
                    var bestAxisGap = double.MaxValue;
                    var bestSpanGap = double.MaxValue;
                    var bestFromRaw = false;
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var c = candidates[i];
                        if (c.AxisGap > axisCutoff)
                        {
                            continue;
                        }

                        if (hasSideCandidate && !IsSideCandidate(c))
                        {
                            continue;
                        }

                        var better = !found;
                        if (!better)
                        {
                            if (c.FromRaw && !bestFromRaw)
                            {
                                better = true;
                            }
                            else if (c.FromRaw == bestFromRaw)
                            {
                                if (preferLowerX)
                                {
                                    if (c.X < (bestX - 1e-6))
                                    {
                                        better = true;
                                    }
                                    else if (Math.Abs(c.X - bestX) <= 1e-6)
                                    {
                                        better =
                                            c.AxisGap < (bestAxisGap - 1e-6) ||
                                            (Math.Abs(c.AxisGap - bestAxisGap) <= 1e-6 && c.SpanGap < bestSpanGap);
                                    }
                                }
                                else
                                {
                                    if (c.X > (bestX + 1e-6))
                                    {
                                        better = true;
                                    }
                                    else if (Math.Abs(c.X - bestX) <= 1e-6)
                                    {
                                        better =
                                            c.AxisGap < (bestAxisGap - 1e-6) ||
                                            (Math.Abs(c.AxisGap - bestAxisGap) <= 1e-6 && c.SpanGap < bestSpanGap);
                                    }
                                }
                            }
                        }

                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestX = c.X;
                        bestAxisGap = c.AxisGap;
                        bestSpanGap = c.SpanGap;
                        bestFromRaw = c.FromRaw;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    targetX = bestX;
                    return true;
                }

                bool TryFindPairedHorizontalMidpointY(Point2d p0, Point2d p1, out double targetY)
                {
                    targetY = 0.5 * (p0.Y + p1.Y);
                    bool TryResolvePairedQsecComponentMidpointY(Point2d a, Point2d b, double? anchorY, out double y)
                    {
                        y = 0.5 * (a.Y + b.Y);
                        if (qsecVerticalComponentTargets.Count == 0 || qsecCenters.Count == 0)
                        {
                            return false;
                        }

                        const double lineXSpanTol = 4.00;
                        const double componentYSpanTol = 80.0;
                        const double componentAxisTol = 20.0;
                        const double anchorTol = 35.0;
                        const double centerTol = 2.50;
                        const double maxMove = 80.0;
                        var lineMinX = Math.Min(a.X, b.X) - lineXSpanTol;
                        var lineMaxX = Math.Max(a.X, b.X) + lineXSpanTol;
                        var lineY = 0.5 * (a.Y + b.Y);
                        var found = false;
                        var bestAnchorGap = double.MaxValue;
                        var bestAxisGap = double.MaxValue;
                        var bestSpanGap = double.MaxValue;
                        var bestMove = double.MaxValue;
                        var bestY = y;

                        for (var ci = 0; ci < qsecVerticalComponentTargets.Count; ci++)
                        {
                            var c = qsecVerticalComponentTargets[ci];
                            var xLine = 0.5 * (c.A.X + c.B.X);
                            if (xLine < lineMinX || xLine > lineMaxX)
                            {
                                continue;
                            }

                            var minY = Math.Min(c.A.Y, c.B.Y);
                            var maxY = Math.Max(c.A.Y, c.B.Y);
                            if (lineY < (minY - componentYSpanTol) || lineY > (maxY + componentYSpanTol))
                            {
                                continue;
                            }

                            var axisGap = Math.Min(Math.Abs(a.X - xLine), Math.Abs(b.X - xLine));
                            if (axisGap > componentAxisTol)
                            {
                                continue;
                            }

                            var spanGap = 0.0;
                            if (lineY < minY)
                            {
                                spanGap = minY - lineY;
                            }
                            else if (lineY > maxY)
                            {
                                spanGap = lineY - maxY;
                            }

                            for (var si = 0; si < qsecCenters.Count; si++)
                            {
                                var center = qsecCenters[si];
                                if (Math.Abs(center.X - xLine) > centerTol ||
                                    center.Y < (minY - centerTol) ||
                                    center.Y > (maxY + centerTol))
                                {
                                    continue;
                                }

                                var sideY = lineY <= center.Y
                                    ? 0.5 * (minY + center.Y)
                                    : 0.5 * (maxY + center.Y);
                                var move = Math.Abs(sideY - lineY);
                                if (move > maxMove)
                                {
                                    continue;
                                }

                                var anchorGap = anchorY.HasValue
                                    ? Math.Abs(sideY - anchorY.Value)
                                    : 0.0;
                                if (anchorY.HasValue && anchorGap > anchorTol)
                                {
                                    continue;
                                }

                                var better =
                                    !found ||
                                    (anchorY.HasValue && anchorGap < (bestAnchorGap - 1e-6)) ||
                                    (anchorY.HasValue && Math.Abs(anchorGap - bestAnchorGap) <= 1e-6 &&
                                        (axisGap < (bestAxisGap - 1e-6) ||
                                         (Math.Abs(axisGap - bestAxisGap) <= 1e-6 &&
                                          (spanGap < (bestSpanGap - 1e-6) ||
                                           (Math.Abs(spanGap - bestSpanGap) <= 1e-6 && move < bestMove))))) ||
                                    (!anchorY.HasValue &&
                                        (axisGap < (bestAxisGap - 1e-6) ||
                                         (Math.Abs(axisGap - bestAxisGap) <= 1e-6 &&
                                          (spanGap < (bestSpanGap - 1e-6) ||
                                           (Math.Abs(spanGap - bestSpanGap) <= 1e-6 && move < bestMove)))));
                                if (!better)
                                {
                                    continue;
                                }

                                found = true;
                                bestAnchorGap = anchorGap;
                                bestAxisGap = axisGap;
                                bestSpanGap = spanGap;
                                bestMove = move;
                                bestY = sideY;
                            }
                        }

                        if (!found)
                        {
                            return false;
                        }

                        y = bestY;
                        return true;
                    }

                    bool TryResolveBracketedVerticalMidpointY(Point2d a, Point2d b, double? anchorY, out double y)
                    {
                        y = 0.5 * (a.Y + b.Y);
                        if (verticalMidpointTargetSegments.Count == 0)
                        {
                            return false;
                        }

                        const double axisTol = 16.00;
                        const double spanTol = 40.0;
                        const double bracketSpanMax = 10.0;
                        const double nearRefTol = 12.0;
                        var lineY = 0.5 * (a.Y + b.Y);
                        var referenceY = anchorY ?? lineY;
                        var foundLower = false;
                        var foundUpper = false;
                        var lowerY = referenceY;
                        var upperY = referenceY;
                        var lowerGap = double.MaxValue;
                        var upperGap = double.MaxValue;
                        var lowerAxisGap = double.MaxValue;
                        var upperAxisGap = double.MaxValue;
                        for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                        {
                            var seg = verticalMidpointTargetSegments[i];
                            // Use deterministic section/usec vertical midpoint bands only.
                            // Skip raw quarter/component bands to avoid 100m-extension bleed.
                            if (seg.Priority <= 0 || seg.Priority >= 3)
                            {
                                continue;
                            }

                            var minY = Math.Min(seg.A.Y, seg.B.Y);
                            var maxY = Math.Max(seg.A.Y, seg.B.Y);
                            if (lineY < (minY - spanTol) || lineY > (maxY + spanTol))
                            {
                                continue;
                            }

                            var dy = seg.B.Y - seg.A.Y;
                            var t = 0.5;
                            if (Math.Abs(dy) > 1e-6)
                            {
                                t = (lineY - seg.A.Y) / dy;
                            }

                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            var xAtLineY = seg.A.X + ((seg.B.X - seg.A.X) * t);
                            var axisGap = Math.Min(Math.Abs(a.X - xAtLineY), Math.Abs(b.X - xAtLineY));
                            if (axisGap > axisTol)
                            {
                                continue;
                            }

                            var candidateY = seg.Mid.Y;
                            var refGap = Math.Abs(candidateY - referenceY);
                            if (refGap > nearRefTol)
                            {
                                continue;
                            }

                            if (candidateY <= referenceY)
                            {
                                var betterLower =
                                    !foundLower ||
                                    refGap < (lowerGap - 1e-6) ||
                                    (Math.Abs(refGap - lowerGap) <= 1e-6 && axisGap < lowerAxisGap);
                                if (!betterLower)
                                {
                                    continue;
                                }

                                foundLower = true;
                                lowerY = candidateY;
                                lowerGap = refGap;
                                lowerAxisGap = axisGap;
                            }
                            else
                            {
                                var betterUpper =
                                    !foundUpper ||
                                    refGap < (upperGap - 1e-6) ||
                                    (Math.Abs(refGap - upperGap) <= 1e-6 && axisGap < upperAxisGap);
                                if (!betterUpper)
                                {
                                    continue;
                                }

                                foundUpper = true;
                                upperY = candidateY;
                                upperGap = refGap;
                                upperAxisGap = axisGap;
                            }
                        }

                        if (!foundLower || !foundUpper)
                        {
                            return false;
                        }

                        if ((upperY - lowerY) > bracketSpanMax)
                        {
                            return false;
                        }

                        y = 0.5 * (lowerY + upperY);
                        return true;
                    }

                    bool TryResolveAxisBracketedVerticalMidpointY(Point2d axisEndpoint, double anchorY, out double y)
                    {
                        y = anchorY;
                        if (verticalMidpointTargetSegments.Count == 0)
                        {
                            return false;
                        }

                        const double axisTol = 4.00;
                        const double spanTol = 40.0;
                        const double nearAnchorTol = 12.0;
                        const double bracketSpanMax = 10.0;
                        var foundLower = false;
                        var foundUpper = false;
                        var lowerY = anchorY;
                        var upperY = anchorY;
                        var lowerGap = double.MaxValue;
                        var upperGap = double.MaxValue;
                        var lowerAxisGap = double.MaxValue;
                        var upperAxisGap = double.MaxValue;

                        for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                        {
                            var seg = verticalMidpointTargetSegments[i];
                            if (seg.Priority <= 0 || seg.Priority >= 3)
                            {
                                continue;
                            }

                            var minY = Math.Min(seg.A.Y, seg.B.Y);
                            var maxY = Math.Max(seg.A.Y, seg.B.Y);
                            if (anchorY < (minY - spanTol) || anchorY > (maxY + spanTol))
                            {
                                continue;
                            }

                            var dy = seg.B.Y - seg.A.Y;
                            var t = 0.5;
                            if (Math.Abs(dy) > 1e-6)
                            {
                                t = (anchorY - seg.A.Y) / dy;
                            }

                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            var xAtAnchorY = seg.A.X + ((seg.B.X - seg.A.X) * t);
                            var axisGap = Math.Abs(axisEndpoint.X - xAtAnchorY);
                            if (axisGap > axisTol)
                            {
                                continue;
                            }

                            var candidateY = seg.Mid.Y;
                            var anchorGap = Math.Abs(candidateY - anchorY);
                            if (anchorGap > nearAnchorTol)
                            {
                                continue;
                            }

                            if (candidateY <= anchorY)
                            {
                                var betterLower =
                                    !foundLower ||
                                    anchorGap < (lowerGap - 1e-6) ||
                                    (Math.Abs(anchorGap - lowerGap) <= 1e-6 && axisGap < lowerAxisGap);
                                if (!betterLower)
                                {
                                    continue;
                                }

                                foundLower = true;
                                lowerY = candidateY;
                                lowerGap = anchorGap;
                                lowerAxisGap = axisGap;
                            }
                            else
                            {
                                var betterUpper =
                                    !foundUpper ||
                                    anchorGap < (upperGap - 1e-6) ||
                                    (Math.Abs(anchorGap - upperGap) <= 1e-6 && axisGap < upperAxisGap);
                                if (!betterUpper)
                                {
                                    continue;
                                }

                                foundUpper = true;
                                upperY = candidateY;
                                upperGap = anchorGap;
                                upperAxisGap = axisGap;
                            }
                        }

                        if (!foundLower || !foundUpper)
                        {
                            return false;
                        }

                        if ((upperY - lowerY) > bracketSpanMax)
                        {
                            return false;
                        }

                        y = 0.5 * (lowerY + upperY);
                        return true;
                    }

                    bool TryResolveAxisNeighborVerticalMidpointY(Point2d axisEndpoint, double anchorY, out double y)
                    {
                        y = anchorY;
                        if (verticalMidpointTargetSegments.Count == 0)
                        {
                            return false;
                        }

                        const double axisTol = 4.00;
                        const double spanTol = 40.0;
                        const double nearAnchorTol = 12.0;
                        const double sameRowTol = 0.05;
                        var found = false;
                        var bestY = anchorY;
                        var bestGap = double.MaxValue;
                        var bestPriority = int.MaxValue;
                        var bestAxisGap = double.MaxValue;

                        for (var i = 0; i < verticalMidpointTargetSegments.Count; i++)
                        {
                            var seg = verticalMidpointTargetSegments[i];
                            if (seg.Priority <= 0 || seg.Priority >= 3)
                            {
                                continue;
                            }

                            var minY = Math.Min(seg.A.Y, seg.B.Y);
                            var maxY = Math.Max(seg.A.Y, seg.B.Y);
                            if (anchorY < (minY - spanTol) || anchorY > (maxY + spanTol))
                            {
                                continue;
                            }

                            var dy = seg.B.Y - seg.A.Y;
                            var t = 0.5;
                            if (Math.Abs(dy) > 1e-6)
                            {
                                t = (anchorY - seg.A.Y) / dy;
                            }

                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            var xAtAnchorY = seg.A.X + ((seg.B.X - seg.A.X) * t);
                            var axisGap = Math.Abs(axisEndpoint.X - xAtAnchorY);
                            if (axisGap > axisTol)
                            {
                                continue;
                            }

                            var candidateY = seg.Mid.Y;
                            var rowGap = Math.Abs(candidateY - anchorY);
                            if (rowGap <= sameRowTol || rowGap > nearAnchorTol)
                            {
                                continue;
                            }

                            var better =
                                !found ||
                                rowGap < (bestGap - 1e-6) ||
                                (Math.Abs(rowGap - bestGap) <= 1e-6 && seg.Priority < bestPriority) ||
                                (Math.Abs(rowGap - bestGap) <= 1e-6 && seg.Priority == bestPriority && axisGap < bestAxisGap);
                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestY = candidateY;
                            bestGap = rowGap;
                            bestPriority = seg.Priority;
                            bestAxisGap = axisGap;
                        }

                        if (!found)
                        {
                            return false;
                        }

                        y = bestY;
                        return true;
                    }

                    bool TryResolveVerticalQsecSouthHalfRowY(Point2d endpoint, out double y)
                    {
                        y = endpoint.Y;
                        if (!TryResolveVerticalQsecDirectionalHalfMidpoint(endpoint, out _, out var directionalY))
                        {
                            return false;
                        }

                        y = directionalY;
                        return true;
                    }

                    var found0 = TryFindEndpointVerticalMidpointY(p0, out var y0, out var pri0);
                    var found1 = TryFindEndpointVerticalMidpointY(p1, out var y1, out var pri1);
                    var traceTarget = new Point2d(504123.929, 5986277.757);
                    const double traceTol = 140.0;
                    var traceThis =
                        p0.GetDistanceTo(traceTarget) <= traceTol ||
                        p1.GetDistanceTo(traceTarget) <= traceTol;
                    if (traceThis && logger != null)
                    {
                        logger.WriteLine(
                            $"TRACE-LSD-PAIRY start p0=({p0.X:0.###},{p0.Y:0.###}) p1=({p1.X:0.###},{p1.Y:0.###}) found0={found0} y0={y0:0.###} pri0={pri0} found1={found1} y1={y1:0.###} pri1={pri1}.");
                    }

                    double? componentAnchorY = null;
                    if (found0 && found1)
                    {
                        if (pri0 < pri1)
                        {
                            componentAnchorY = y0;
                        }
                        else if (pri1 < pri0)
                        {
                            componentAnchorY = y1;
                        }
                        else
                        {
                            componentAnchorY = 0.5 * (y0 + y1);
                        }
                    }
                    else if (found0)
                    {
                        componentAnchorY = y0;
                    }
                    else if (found1)
                    {
                        componentAnchorY = y1;
                    }

                    if (componentAnchorY.HasValue &&
                        TryResolvePairedQsecComponentMidpointY(p0, p1, componentAnchorY, out var pairedComponentY))
                    {
                        targetY = pairedComponentY;
                        if (traceThis && logger != null)
                        {
                            var anchorText = componentAnchorY.HasValue ? componentAnchorY.Value.ToString("0.###") : "null";
                            logger.WriteLine($"TRACE-LSD-PAIRY result=component anchorY={anchorText} targetY={targetY:0.###}");
                        }

                        return true;
                    }

                    if (traceThis && logger != null && !componentAnchorY.HasValue)
                    {
                        logger.WriteLine("TRACE-LSD-PAIRY component-skip reason=no-anchor");
                    }

                    if (TryResolveBracketedVerticalMidpointY(p0, p1, componentAnchorY, out var bracketY))
                    {
                        targetY = bracketY;
                        if (traceThis && logger != null)
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY result=bracketed targetY={targetY:0.###}");
                        }

                        return true;
                    }

                    if (found0 && !found1)
                    {
                        if (TryResolveVerticalQsecSouthHalfRowY(p1, out var southHalfY))
                        {
                            targetY = southHalfY;
                            if (traceThis && logger != null)
                            {
                                logger.WriteLine($"TRACE-LSD-PAIRY result=qsec-directional-half targetY={targetY:0.###}");
                            }

                            return true;
                        }

                        if (IsEndpointOnHardBoundary(p0) &&
                            TryResolveAxisNeighborVerticalMidpointY(p1, y0, out var axisNeighborY))
                        {
                            targetY = 0.5 * (y0 + axisNeighborY);
                            if (traceThis && logger != null)
                            {
                                logger.WriteLine(
                                    $"TRACE-LSD-PAIRY result=axis-neighbor-average anchorY={y0:0.###} neighborY={axisNeighborY:0.###} targetY={targetY:0.###}");
                            }

                            return true;
                        }
                        else if (traceThis && logger != null && IsEndpointOnHardBoundary(p0))
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY axis-neighbor-average skipped anchorY={y0:0.###} reason=no-neighbor");
                        }

                        if (TryResolveAxisBracketedVerticalMidpointY(p1, y0, out var axisBracketY))
                        {
                            targetY = axisBracketY;
                            if (traceThis && logger != null)
                            {
                                logger.WriteLine($"TRACE-LSD-PAIRY result=axis-bracketed targetY={targetY:0.###}");
                            }

                            return true;
                        }
                        else if (traceThis && logger != null)
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY axis-bracketed skipped anchorY={y0:0.###} reason=no-bracket");
                        }
                    }

                    if (found1 && !found0)
                    {
                        if (TryResolveVerticalQsecSouthHalfRowY(p0, out var southHalfY))
                        {
                            targetY = southHalfY;
                            if (traceThis && logger != null)
                            {
                                logger.WriteLine($"TRACE-LSD-PAIRY result=qsec-directional-half targetY={targetY:0.###}");
                            }

                            return true;
                        }

                        if (IsEndpointOnHardBoundary(p1) &&
                            TryResolveAxisNeighborVerticalMidpointY(p0, y1, out var axisNeighborY))
                        {
                            targetY = 0.5 * (y1 + axisNeighborY);
                            if (traceThis && logger != null)
                            {
                                logger.WriteLine(
                                    $"TRACE-LSD-PAIRY result=axis-neighbor-average anchorY={y1:0.###} neighborY={axisNeighborY:0.###} targetY={targetY:0.###}");
                            }

                            return true;
                        }
                        else if (traceThis && logger != null && IsEndpointOnHardBoundary(p1))
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY axis-neighbor-average skipped anchorY={y1:0.###} reason=no-neighbor");
                        }

                        if (TryResolveAxisBracketedVerticalMidpointY(p0, y1, out var axisBracketY))
                        {
                            targetY = axisBracketY;
                            if (traceThis && logger != null)
                            {
                                logger.WriteLine($"TRACE-LSD-PAIRY result=axis-bracketed targetY={targetY:0.###}");
                            }

                            return true;
                        }
                        else if (traceThis && logger != null)
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY axis-bracketed skipped anchorY={y1:0.###} reason=no-bracket");
                        }
                    }

                    if (!found0 && !found1)
                    {
                        if (traceThis && logger != null)
                        {
                            logger.WriteLine("TRACE-LSD-PAIRY result=none");
                        }

                        return false;
                    }

                    if (found0 && !found1)
                    {
                        targetY = y0;
                        if (traceThis && logger != null)
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY result=single0 targetY={targetY:0.###}");
                        }

                        return true;
                    }

                    if (found1 && !found0)
                    {
                        targetY = y1;
                        if (traceThis && logger != null)
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY result=single1 targetY={targetY:0.###}");
                        }

                        return true;
                    }

                    // When both sides resolve near the same row, force a shared midpoint Y.
                    // This prevents side-priority from biasing one endpoint low/high.
                    const double sharedRowAverageTol = 4.00;
                    if (Math.Abs(y0 - y1) <= sharedRowAverageTol)
                    {
                        targetY = 0.5 * (y0 + y1);
                        if (traceThis && logger != null)
                        {
                            logger.WriteLine($"TRACE-LSD-PAIRY result=shared-average targetY={targetY:0.###}");
                        }

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

                    if (traceThis && logger != null)
                    {
                        logger.WriteLine($"TRACE-LSD-PAIRY result=final targetY={targetY:0.###}");
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

                    var sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    var sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
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
                        var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                        var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);

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

                bool TryFindCorrectionAdjacentSnapTarget(
                    Point2d endpoint,
                    Point2d other,
                    bool sourceOnCorrectionOuter,
                    out Point2d target)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    var sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
                    if (!sourceHorizontal && !sourceVertical)
                    {
                        return false;
                    }

                    var traceThisEndpoint = logger != null;

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var bestPrimaryScore = double.MaxValue;
                    var bestAlongFromOther = double.MaxValue;
                    var bestLateral = double.MaxValue;
                    var bestMove = double.MaxValue;
                    var bestTarget = endpoint;
                    var perpDir = new Vector2d(-outwardDir.Y, outwardDir.X);
                    // Correction-adjacent endpoints can be displaced farther before this pass runs
                    // (especially near section 5/6 seams). Keep relaxed gates correction-only.
                    const double correctionMinMove = 0.05;
                    const double correctionAxisTol = 40.0;
                    const double correctionAxisTolRelaxed = 90.0;
                    const double correctionMaxMove = 120.0;
                    const double correctionMaxProjectedShift = 120.0;

                    if (traceThisEndpoint && logger != null)
                    {
                        logger.WriteLine(
                            $"TRACE-LSD-CORR start endpoint=({endpoint.X:0.###},{endpoint.Y:0.###}) other=({other.X:0.###},{other.Y:0.###}) srcH={sourceHorizontal} srcV={sourceVertical} onCorrOuter={sourceOnCorrectionOuter} corrSegs={correctionBoundarySegments.Count} corrOuterSegs={correctionOuterBoundarySegments.Count} hardSegs={hardBoundarySegments.Count}.");
                    }

                    var preferredCorrectionSegments =
                        sourceVertical && correctionBoundarySegments.Count > 0
                            ? correctionBoundarySegments
                            : (sourceOnCorrectionOuter && correctionOuterBoundarySegments.Count > 0
                                ? correctionOuterBoundarySegments
                                : correctionBoundarySegments);
                    var directionalCorrectionSegments =
                        correctionBoundarySegments.Count > 0
                            ? correctionBoundarySegments
                            : preferredCorrectionSegments;

                    Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b) =>
                        ClosestPointOnSegmentForEndpointEnforcement(p, a, b);

                    bool EndpointTouchesQsec(Point2d p)
                    {
                        const double qsecTouchTol = 1.0;
                        if (sourceVertical)
                        {
                            for (var i = 0; i < qsecVerticalSegments.Count; i++)
                            {
                                var q = qsecVerticalSegments[i];
                                var minY = Math.Min(q.A.Y, q.B.Y) - qsecTouchTol;
                                var maxY = Math.Max(q.A.Y, q.B.Y) + qsecTouchTol;
                                if (p.Y < minY || p.Y > maxY)
                                {
                                    continue;
                                }

                                if (Math.Abs(p.X - q.A.X) <= qsecTouchTol)
                                {
                                    return true;
                                }
                            }

                            return false;
                        }

                        for (var i = 0; i < qsecHorizontalSegments.Count; i++)
                        {
                            var q = qsecHorizontalSegments[i];
                            var minX = Math.Min(q.A.X, q.B.X) - qsecTouchTol;
                            var maxX = Math.Max(q.A.X, q.B.X) + qsecTouchTol;
                            if (p.X < minX || p.X > maxX)
                            {
                                continue;
                            }

                            if (Math.Abs(p.Y - q.A.Y) <= qsecTouchTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    bool SegmentAnchoredToQsec((Point2d A, Point2d B) seg)
                    {
                        return EndpointTouchesQsec(seg.A) || EndpointTouchesQsec(seg.B);
                    }

                    bool TrySelectQsecAnchoredCorrectionZeroMidpoint(bool requireQsecAnchor, out Point2d anchoredTarget)
                    {
                        anchoredTarget = endpoint;
                        if (!sourceVertical || correctionBoundarySegments.Count == 0)
                        {
                            return false;
                        }

                        var towardOtherVec = other - endpoint;
                        var towardOtherLen = towardOtherVec.Length;
                        if (towardOtherLen <= 1e-6)
                        {
                            return false;
                        }

                        var towardOtherDir = towardOtherVec / towardOtherLen;
                        const double correctionAxisLevelTol = 0.60;
                        var rawCandidates = new List<(Point2d Mid, double Move, double AxisOffset, double TowardOther, double AxisValue)>();

                        for (var i = 0; i < correctionBoundarySegments.Count; i++)
                        {
                            var seg = correctionBoundarySegments[i];
                            var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                            var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);
                            if (sourceVertical && !segHorizontal)
                            {
                                continue;
                            }

                            if (sourceHorizontal && !segVertical)
                            {
                                continue;
                            }

                            if (requireQsecAnchor && !SegmentAnchoredToQsec(seg))
                            {
                                continue;
                            }

                            var probeOnSegment = ClosestPointOnSegment(endpoint, seg.A, seg.B);
                            var move = endpoint.GetDistanceTo(probeOnSegment);
                            if (move <= correctionMinMove || move > correctionMaxMove)
                            {
                                continue;
                            }

                            var axisOffset = sourceVertical
                                ? Math.Abs(probeOnSegment.X - endpoint.X)
                                : Math.Abs(probeOnSegment.Y - endpoint.Y);
                            if (axisOffset > correctionAxisTolRelaxed)
                            {
                                continue;
                            }

                            var towardOther = (probeOnSegment - endpoint).DotProduct(towardOtherDir);
                            if (Math.Abs(towardOther) > correctionMaxProjectedShift)
                            {
                                continue;
                            }

                            var midpoint = new Point2d(
                                (seg.A.X + seg.B.X) * 0.5,
                                (seg.A.Y + seg.B.Y) * 0.5);
                            if (midpoint.GetDistanceTo(other) < minRemainingLength)
                            {
                                continue;
                            }

                            var axisValue = sourceVertical ? midpoint.Y : midpoint.X;
                            rawCandidates.Add((midpoint, move, axisOffset, towardOther, axisValue));
                        }

                        if (rawCandidates.Count == 0)
                        {
                            return false;
                        }

                        // For correction-outer vertical LSD endpoints, pick C-0 midpoint strictly
                        // on the endpoint->other side (north/south), then nearest local station.
                        if (sourceVertical)
                        {
                            var endpointAxis = endpoint.Y;
                            var desiredOtherAxis = other.Y;
                            var wantHigherByEndpoint = desiredOtherAxis >= endpointAxis;
                            var sideStrict = rawCandidates
                                .Where(c => wantHigherByEndpoint
                                    ? c.AxisValue >= (endpointAxis + correctionAxisLevelTol)
                                    : c.AxisValue <= (endpointAxis - correctionAxisLevelTol))
                                .ToList();
                            var sideRelaxed = rawCandidates
                                .Where(c => wantHigherByEndpoint
                                    ? c.AxisValue >= (endpointAxis - correctionAxisLevelTol)
                                    : c.AxisValue <= (endpointAxis + correctionAxisLevelTol))
                                .ToList();
                            var sidePool = sideStrict.Count > 0 ? sideStrict : sideRelaxed;
                            if (sidePool.Count == 0)
                            {
                                return false;
                            }

                            var directedPool = sidePool
                                .Where(c => c.TowardOther > correctionMinMove)
                                .ToList();
                            if (directedPool.Count == 0)
                            {
                                directedPool = sidePool;
                            }

                            var extremeAxis = wantHigherByEndpoint
                                ? directedPool.Max(c => c.AxisValue)
                                : directedPool.Min(c => c.AxisValue);
                            var extremeBand = directedPool
                                .Where(c => Math.Abs(c.AxisValue - extremeAxis) <= correctionAxisLevelTol)
                                .ToList();
                            if (extremeBand.Count == 0)
                            {
                                extremeBand = directedPool;
                            }

                            var bestOuter = extremeBand
                                .OrderBy(c => c.AxisOffset)
                                .ThenByDescending(c => c.TowardOther)
                                .ThenBy(c => c.Move)
                                .First();
                            anchoredTarget = bestOuter.Mid;
                            if (traceThisEndpoint && logger != null)
                            {
                                logger.WriteLine(
                                    $"TRACE-LSD-CORR qsec-anchored target=({anchoredTarget.X:0.###},{anchoredTarget.Y:0.###}) move={bestOuter.Move:0.###} axis={bestOuter.AxisOffset:0.###} toward={bestOuter.TowardOther:0.###} wantHigh={wantHigherByEndpoint} requireQsec={requireQsecAnchor}.");
                            }

                            return true;
                        }

                        var byAxis = rawCandidates
                            .OrderBy(c => c.AxisValue)
                            .ThenBy(c => c.TowardOther)
                            .ThenBy(c => c.Move)
                            .ToList();
                        var uniqueByAxis = new List<(Point2d Mid, double Move, double AxisOffset, double TowardOther, double AxisValue)>();
                        for (var i = 0; i < byAxis.Count; i++)
                        {
                            var c = byAxis[i];
                            if (uniqueByAxis.Count == 0)
                            {
                                uniqueByAxis.Add(c);
                                continue;
                            }

                            var last = uniqueByAxis[uniqueByAxis.Count - 1];
                            if (Math.Abs(c.AxisValue - last.AxisValue) > correctionAxisLevelTol)
                            {
                                uniqueByAxis.Add(c);
                                continue;
                            }

                            if (c.TowardOther < last.TowardOther - 1e-9 ||
                                (Math.Abs(c.TowardOther - last.TowardOther) <= 1e-9 &&
                                 c.Move < last.Move - 1e-9))
                            {
                                uniqueByAxis[uniqueByAxis.Count - 1] = c;
                            }
                        }

                        var axisValues = uniqueByAxis.Select(c => c.AxisValue).OrderBy(v => v).ToList();
                        var mid = axisValues.Count / 2;
                        var centerAxis = axisValues.Count % 2 == 0
                            ? 0.5 * (axisValues[mid - 1] + axisValues[mid])
                            : axisValues[mid];
                        var otherAxis = sourceVertical ? other.Y : other.X;
                        var wantHigherAxis = otherAxis >= centerAxis;
                        var sideCandidates = new List<(Point2d Mid, double Move, double AxisOffset, double TowardOther, double AxisValue)>();
                        for (var i = 0; i < uniqueByAxis.Count; i++)
                        {
                            var c = uniqueByAxis[i];
                            var onWantedSide = wantHigherAxis
                                ? c.AxisValue >= (centerAxis + correctionAxisLevelTol)
                                : c.AxisValue <= (centerAxis - correctionAxisLevelTol);
                            if (onWantedSide)
                            {
                                sideCandidates.Add(c);
                            }
                        }

                        var pool = sideCandidates.Count > 0 ? sideCandidates : uniqueByAxis;
                        var best = pool
                            .OrderBy(c => c.Move)
                            .ThenBy(c => c.AxisOffset)
                            .ThenBy(c => c.TowardOther)
                            .First();

                        anchoredTarget = best.Mid;
                        if (traceThisEndpoint && logger != null)
                        {
                            logger.WriteLine(
                                $"TRACE-LSD-CORR qsec-anchored target=({anchoredTarget.X:0.###},{anchoredTarget.Y:0.###}) move={best.Move:0.###} axis={best.AxisOffset:0.###} toward={best.TowardOther:0.###} wantHigh={wantHigherAxis} requireQsec={requireQsecAnchor}.");
                        }

                        return true;
                    }

                    bool TrySelectDirectionalCorrectionTarget(out Point2d directionalTarget)
                    {
                        directionalTarget = endpoint;
                        if (directionalCorrectionSegments.Count == 0)
                        {
                            return false;
                        }

                        var towardOtherVec = other - endpoint;
                        var towardOtherLen = towardOtherVec.Length;
                        if (towardOtherLen <= 1e-6)
                        {
                            return false;
                        }

                        var towardOtherDir = towardOtherVec / towardOtherLen;
                        var candidates = new List<(Point2d Point, double TowardOther, double Move, double AxisOffset, double AxisValue)>();

                        for (var i = 0; i < directionalCorrectionSegments.Count; i++)
                        {
                            var seg = directionalCorrectionSegments[i];
                            var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                            var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);
                            if (sourceVertical && !segHorizontal)
                            {
                                continue;
                            }

                            if (sourceHorizontal && !segVertical)
                            {
                                continue;
                            }

                            var probeOnSegment = ClosestPointOnSegment(endpoint, seg.A, seg.B);
                            var midpoint = new Point2d(
                                (seg.A.X + seg.B.X) * 0.5,
                                (seg.A.Y + seg.B.Y) * 0.5);
                            // For correction-outer directional tie-ins, the target station must be
                            // the chosen correction segment midpoint (required line midpoint), while
                            // eligibility/scoring still use the local projection.
                            var candidate = sourceOnCorrectionOuter ? midpoint : probeOnSegment;
                            var axisOffset = sourceVertical
                                ? Math.Abs(probeOnSegment.X - endpoint.X)
                                : Math.Abs(probeOnSegment.Y - endpoint.Y);
                            if (axisOffset > correctionAxisTolRelaxed)
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(probeOnSegment);
                            if (move <= correctionMinMove || move > correctionMaxMove)
                            {
                                continue;
                            }

                            // Directional correction targeting: keep movement toward the opposite LSD
                            // endpoint so near-edge tie-ins can still resolve when outward-only gating
                            // would reject the only local correction candidate.
                            var towardOther = (probeOnSegment - endpoint).DotProduct(towardOtherDir);
                            if (towardOther <= correctionMinMove || towardOther > correctionMaxProjectedShift)
                            {
                                continue;
                            }

                            if (candidate.GetDistanceTo(other) < minRemainingLength)
                            {
                                continue;
                            }

                            var axisValue = sourceVertical ? candidate.Y : candidate.X;
                            candidates.Add((candidate, towardOther, move, axisOffset, axisValue));
                        }

                        if (candidates.Count == 0)
                        {
                            return false;
                        }

                        const double correctionAxisLevelTol = 0.60;
                        var byAxis = candidates
                            .OrderBy(c => c.AxisValue)
                            .ThenBy(c => c.TowardOther)
                            .ToList();
                        var uniqueByAxis = new List<(Point2d Point, double TowardOther, double Move, double AxisOffset, double AxisValue)>();
                        for (var i = 0; i < byAxis.Count; i++)
                        {
                            var c = byAxis[i];
                            if (uniqueByAxis.Count == 0)
                            {
                                uniqueByAxis.Add(c);
                                continue;
                            }

                            var last = uniqueByAxis[uniqueByAxis.Count - 1];
                            if (Math.Abs(c.AxisValue - last.AxisValue) > correctionAxisLevelTol)
                            {
                                uniqueByAxis.Add(c);
                                continue;
                            }

                            if (c.TowardOther < last.TowardOther - 1e-9)
                            {
                                uniqueByAxis[uniqueByAxis.Count - 1] = c;
                            }
                        }

                        var axisValues = uniqueByAxis.Select(c => c.AxisValue).OrderBy(v => v).ToList();
                        var mid = axisValues.Count / 2;
                        var centerAxis = axisValues.Count % 2 == 0
                            ? 0.5 * (axisValues[mid - 1] + axisValues[mid])
                            : axisValues[mid];
                        var otherAxis = sourceVertical ? other.Y : other.X;
                        var endpointAxis = sourceVertical ? endpoint.Y : endpoint.X;
                        var wantHighSide = otherAxis >= centerAxis;
                        var sideCandidates = new List<(Point2d Point, double TowardOther, double Move, double AxisOffset, double AxisValue)>();
                        for (var i = 0; i < uniqueByAxis.Count; i++)
                        {
                            var c = uniqueByAxis[i];
                            var onWantedSide = wantHighSide
                                ? c.AxisValue >= (centerAxis + correctionAxisLevelTol)
                                : c.AxisValue <= (centerAxis - correctionAxisLevelTol);
                            if (onWantedSide)
                            {
                                sideCandidates.Add(c);
                            }
                        }

                        var minAxis = axisValues[0];
                        var maxAxis = axisValues[axisValues.Count - 1];
                        var endpointOutsideBand = endpointAxis < (minAxis - correctionAxisLevelTol) ||
                                                  endpointAxis > (maxAxis + correctionAxisLevelTol);

                        var scoringPool = sideCandidates.Count > 0 ? sideCandidates : uniqueByAxis;
                        if (endpointOutsideBand && uniqueByAxis.Count >= 2)
                        {
                            // If endpoint is outside the correction pair band, snap to the nearest
                            // correction candidate (near-side boundary), not the far-side member.
                            scoringPool = uniqueByAxis
                                .OrderBy(c => c.Move)
                                .ThenBy(c => c.TowardOther)
                                .ThenBy(c => c.AxisOffset)
                                .ToList();
                        }
                        else
                        {
                            scoringPool.Sort((a, b) =>
                            {
                                var c = a.TowardOther.CompareTo(b.TowardOther);
                                if (c != 0) return c;
                                c = a.Move.CompareTo(b.Move);
                                if (c != 0) return c;
                                return a.AxisOffset.CompareTo(b.AxisOffset);
                            });
                        }

                        if (endpointOutsideBand && uniqueByAxis.Count >= 2)
                        {
                            // already sorted by nearest move
                        }

                        directionalTarget = scoringPool[0].Point;
                        if (traceThisEndpoint && logger != null)
                        {
                            logger.WriteLine(
                                $"TRACE-LSD-CORR directional candidates={candidates.Count} unique={uniqueByAxis.Count} side={sideCandidates.Count} centerAxis={centerAxis:0.###} otherAxis={otherAxis:0.###} wantHigh={wantHighSide} bestTowardOther={scoringPool[0].TowardOther:0.###} bestMove={scoringPool[0].Move:0.###} bestAxis={scoringPool[0].AxisOffset:0.###}" +
                                $" target=({directionalTarget.X:0.###},{directionalTarget.Y:0.###}).");
                        }

                        return true;
                    }

                    if (TrySelectQsecAnchoredCorrectionZeroMidpoint(false, out var anchoredCorrectionTarget) ||
                        TrySelectQsecAnchoredCorrectionZeroMidpoint(true, out anchoredCorrectionTarget))
                    {
                        target = anchoredCorrectionTarget;
                        if (traceThisEndpoint && logger != null)
                        {
                            logger.WriteLine(
                                $"TRACE-LSD-CORR result=qsec-anchored target=({target.X:0.###},{target.Y:0.###}).");
                        }

                        return true;
                    }

                    if (TrySelectDirectionalCorrectionTarget(out var directionalCorrectionTarget))
                    {
                        target = directionalCorrectionTarget;
                        if (traceThisEndpoint && logger != null)
                        {
                            logger.WriteLine(
                                $"TRACE-LSD-CORR result=directional target=({target.X:0.###},{target.Y:0.###}).");
                        }

                        return true;
                    }

                    void ConsiderBoundarySegment(Point2d a, Point2d b, bool fromCorrection, double? lateralTolOverride = null)
                    {
                        var segHorizontal = IsHorizontalLikeForEndpointEnforcement(a, b);
                        var segVertical = IsVerticalLikeForEndpointEnforcement(a, b);
                        if (sourceHorizontal && !segVertical)
                        {
                            return;
                        }

                        if (sourceVertical && !segHorizontal)
                        {
                            return;
                        }

                        Point2d candidate;
                        if (fromCorrection)
                        {
                            // Correction-only: evaluate the intersection of the current LSD axis
                            // with the correction segment so we choose the correct local line at this
                            // endpoint station (not a distant segment midpoint).
                            if (!TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, a, b, out var axisT))
                            {
                                return;
                            }

                            candidate = endpoint + (outwardDir * axisT);
                        }
                        else
                        {
                            candidate = Midpoint(a, b);
                        }
                        var delta = candidate - endpoint;
                        var move = delta.Length;
                        var maxCandidateMove = fromCorrection ? correctionMaxMove : maxMove;
                        if (move <= minMove || move > maxCandidateMove)
                        {
                            return;
                        }

                        var lateral = Math.Abs(delta.DotProduct(perpDir));
                        var lateralTol = fromCorrection ? correctionAxisTol : midpointAxisTol;
                        if (lateralTolOverride.HasValue)
                        {
                            lateralTol = lateralTolOverride.Value;
                        }

                        if (lateral > lateralTol)
                        {
                            return;
                        }

                        var alongFromOther = (candidate - other).DotProduct(outwardDir);
                        if (alongFromOther < minRemainingLength)
                        {
                            return;
                        }

                        var t = alongFromOther - outwardLen;
                        var absT = Math.Abs(t);
                        var maxProjectedShift = fromCorrection ? correctionMaxProjectedShift : maxMove;
                        if (absT <= minMove || absT > maxProjectedShift)
                        {
                            return;
                        }

                        // Correction-adjacent directional rule:
                        // move from the endpoint away from the opposite LSD end toward the inner
                        // correction line on that side of the corridor.
                        if (fromCorrection && t <= minMove)
                        {
                            return;
                        }

                        // Correction-specific directional rule:
                        // choose the nearest valid correction line in the allowed outward direction
                        // (the inner line for that half-corridor).
                        var primaryScore = fromCorrection
                            ? t
                            : alongFromOther;

                        if (traceThisEndpoint && logger != null)
                        {
                            logger.WriteLine(
                                $"TRACE-LSD-CORR cand fromCorr={fromCorrection} mid=({candidate.X:0.###},{candidate.Y:0.###}) move={move:0.###} lat={lateral:0.###} alongOther={alongFromOther:0.###} primary={primaryScore:0.###} t={t:0.###}.");
                        }

                        var better =
                            !found ||
                            primaryScore < (bestPrimaryScore - 1e-6) ||
                            (Math.Abs(primaryScore - bestPrimaryScore) <= 1e-6 &&
                                (lateral < (bestLateral - 1e-6) ||
                                 (Math.Abs(lateral - bestLateral) <= 1e-6 && move < bestMove)));
                        if (!better)
                        {
                            return;
                        }

                        found = true;
                        bestPrimaryScore = primaryScore;
                        bestAlongFromOther = alongFromOther;
                        bestLateral = lateral;
                        bestMove = move;
                        bestTarget = candidate;
                    }

                    // Correction-adjacent rule: evaluate correction boundaries first.
                    for (var i = 0; i < preferredCorrectionSegments.Count; i++)
                    {
                        var seg = preferredCorrectionSegments[i];
                        ConsiderBoundarySegment(seg.A, seg.B, fromCorrection: true);
                    }

                    // Some correction rows are slightly skewed and valid north-side targets can sit
                    // outside the strict axis tolerance. Retry correction-only with a wider gate
                    // before falling back to generic hard boundaries.
                    if (!found)
                    {
                        for (var i = 0; i < preferredCorrectionSegments.Count; i++)
                        {
                            var seg = preferredCorrectionSegments[i];
                            ConsiderBoundarySegment(seg.A, seg.B, fromCorrection: true, lateralTolOverride: correctionAxisTolRelaxed);
                        }
                    }

                    if (!found)
                    {
                        if (traceThisEndpoint && logger != null)
                        {
                            logger.WriteLine("TRACE-LSD-CORR result=none");
                        }

                        return false;
                    }

                    target = bestTarget;
                    if (traceThisEndpoint && logger != null)
                    {
                        logger.WriteLine(
                            $"TRACE-LSD-CORR result=chosen target=({target.X:0.###},{target.Y:0.###}) primary={bestPrimaryScore:0.###} alongOther={bestAlongFromOther:0.###} lat={bestLateral:0.###} move={bestMove:0.###}.");
                    }

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

                // Midpoint-only variant that preserves LSD axis and side preference.
                // Used for horizontal endpoints currently parked on 30.18 so they land on
                // the correct 0/20 hard-boundary midpoint rather than a non-midpoint projection.
                bool TryFindPreferredHardBoundaryMidpoint(
                    Point2d endpoint,
                    Point2d other,
                    bool? preferZero,
                    out Point2d target,
                    double? maxMoveOverride = null)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    var sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
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
                    var maxCandidateMove = maxMoveOverride ?? maxMove;

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                        var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);
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
                        if (move <= minMove || move > maxCandidateMove)
                        {
                            continue;
                        }

                        var lateral = Math.Abs(delta.DotProduct(perpDir));
                        if (lateral > midpointAxisTol)
                        {
                            continue;
                        }

                        var projectedFromOther = (midpoint - other).DotProduct(outwardDir);
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
                            bestPreferredTarget = midpoint;
                            foundPreferred = true;
                        }
                        else
                        {
                            if (score >= bestFallbackScore)
                            {
                                continue;
                            }

                            bestFallbackScore = score;
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

                bool IsEndpointOnCorrectionOuterBoundary(Point2d endpoint)
                {
                    for (var i = 0; i < correctionOuterBoundarySegments.Count; i++)
                    {
                        var seg = correctionOuterBoundarySegments[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                // Relaxed midpoint fallback for stubborn horizontal endpoints that remain on/near 30.18.
                // Keeps side preference while allowing greater axis skew.
                bool TryFindPreferredHardBoundaryMidpointRelaxed(
                    Point2d endpoint,
                    Point2d other,
                    bool? preferZero,
                    out Point2d target,
                    double? maxMoveOverride = null)
                {
                    target = endpoint;
                    var sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    var sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
                    if (!sourceHorizontal && !sourceVertical)
                    {
                        return false;
                    }

                    var foundPreferred = false;
                    var bestPreferredScore = double.MaxValue;
                    var bestPreferredTarget = endpoint;
                    var foundFallback = false;
                    var bestFallbackScore = double.MaxValue;
                    var bestFallbackTarget = endpoint;
                    const double relaxedAxisTol = 80.0;
                    var maxCandidateMove = maxMoveOverride ?? maxMove;

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                        var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);
                        if (sourceHorizontal && !segVertical)
                        {
                            continue;
                        }

                        if (sourceVertical && !segHorizontal)
                        {
                            continue;
                        }

                        var midpoint = Midpoint(seg.A, seg.B);
                        var dx = midpoint.X - endpoint.X;
                        var dy = midpoint.Y - endpoint.Y;
                        var move = Math.Sqrt((dx * dx) + (dy * dy));
                        if (move <= minMove || move > maxCandidateMove)
                        {
                            continue;
                        }

                        var axisGap = sourceHorizontal ? Math.Abs(dy) : Math.Abs(dx);
                        if (axisGap > relaxedAxisTol)
                        {
                            continue;
                        }

                        var score = (axisGap * 100.0) + move;
                        var isPreferred = !preferZero.HasValue || seg.IsZero == preferZero.Value;
                        if (isPreferred)
                        {
                            if (score >= bestPreferredScore)
                            {
                                continue;
                            }

                            bestPreferredScore = score;
                            bestPreferredTarget = midpoint;
                            foundPreferred = true;
                        }
                        else
                        {
                            if (score >= bestFallbackScore)
                            {
                                continue;
                            }

                            bestFallbackScore = score;
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

                bool TryFindNearestHardBoundaryPoint(
                    Point2d endpoint,
                    Point2d other,
                    bool? preferZero,
                    out Point2d target,
                    double? lateralTolOverride = null,
                    double? maxMoveOverride = null,
                    bool allowBacktrack = false)
                {
                    target = endpoint;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    var sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
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
                    var lateralTol = lateralTolOverride ?? midpointAxisTol;
                    var maxCandidateMove = maxMoveOverride ?? maxMove;

                    Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b) =>
                        ClosestPointOnSegmentForEndpointEnforcement(p, a, b);

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                        var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);
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
                        if (move <= minMove || move > maxCandidateMove)
                        {
                            continue;
                        }

                        var lateral = Math.Abs(delta.DotProduct(perpDir));
                        if (lateral > lateralTol)
                        {
                            continue;
                        }

                        var projectedFromOther = (candidate - other).DotProduct(outwardDir);
                        if (!allowBacktrack && projectedFromOther < minRemainingLength)
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

                // Fallback for non-correction endpoints that are already touching a hard boundary
                // but are not yet on that segment midpoint.
                bool TryFindCurrentHardBoundaryMidpoint(Point2d endpoint, Point2d other, bool? preferZero, out Point2d target)
                {
                    target = endpoint;
                    var sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    var sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
                    if (!sourceHorizontal && !sourceVertical)
                    {
                        return false;
                    }

                    var foundPreferred = false;
                    var bestPreferredMove = double.MaxValue;
                    var bestPreferredTarget = endpoint;
                    var foundFallback = false;
                    var bestFallbackMove = double.MaxValue;
                    var bestFallbackTarget = endpoint;

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        var segHorizontal = IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B);
                        var segVertical = IsVerticalLikeForEndpointEnforcement(seg.A, seg.B);
                        if (sourceHorizontal && !segVertical)
                        {
                            continue;
                        }

                        if (sourceVertical && !segHorizontal)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, seg.A, seg.B) > endpointTouchTol)
                        {
                            continue;
                        }

                        var midpoint = Midpoint(seg.A, seg.B);
                        var move = endpoint.GetDistanceTo(midpoint);
                        if (move <= minMove || move > maxMove)
                        {
                            continue;
                        }

                        if (midpoint.GetDistanceTo(other) < minRemainingLength)
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

                for (var i = 0; i < lsdLineIds.Count; i++)
                {
                    var id = lsdLineIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var targetStart = p0;
                    var targetEnd = p1;
                    var midpointLockedStart = false;
                    var midpointLockedEnd = false;
                    var lineIdText = FormatLsdEndpointTraceId(id);
                    var startDecision = "none";
                    var endDecision = "none";
                    var startSource = "none";
                    var endSource = "none";

                    if (traceLsdEndpointFlow)
                    {
                        logger?.WriteLine(
                            $"LSD-ENDPT line={lineIdText} pass=main start p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)} orient={ClassifyOrientation(p0, p1)}.");
                    }

                    // Midpoint special case:
                    // anchor LSD endpoints to the midpoint of the line they terminate on
                    // (1/4, blind, or section) before generic hard-boundary snapping.
                    if (IsVerticalLikeForEndpointEnforcement(p0, p1))
                    {
                        var p0CorrectionAdjacentForMidpoint =
                            IsEndpointNearCorrectionBoundary(p0) && IsEndpointOnThirtyBoundary(p0);
                        var p1CorrectionAdjacentForMidpoint =
                            IsEndpointNearCorrectionBoundary(p1) && IsEndpointOnThirtyBoundary(p1);
                        var hasStartMid = false;
                        var hasEndMid = false;
                        var midStartX = p0.X;
                        var midEndX = p1.X;
                        var midStartY = p0.Y;
                        var midEndY = p1.Y;
                        var hasStartRegularBoundaryMid = false;
                        var hasEndRegularBoundaryMid = false;

                        // Prefer exact segment midpoint resolution on regular boundaries first;
                        // only fall back to quarter/component midpoint logic when unresolved.
                        if (!p0CorrectionAdjacentForMidpoint &&
                            TryFindEndpointRegularHorizontalBoundaryMidpoint(
                                p0,
                                out var regularStartX,
                                out var regularStartY,
                                out _))
                        {
                            hasStartMid = true;
                            midStartX = regularStartX;
                            midStartY = regularStartY;
                            hasStartRegularBoundaryMid = true;
                        }

                        if (!p1CorrectionAdjacentForMidpoint &&
                            TryFindEndpointRegularHorizontalBoundaryMidpoint(
                                p1,
                                out var regularEndX,
                                out var regularEndY,
                                out _))
                        {
                            hasEndMid = true;
                            midEndX = regularEndX;
                            midEndY = regularEndY;
                            hasEndRegularBoundaryMid = true;
                        }

                        if (!hasStartRegularBoundaryMid)
                        {
                            hasStartMid = TryFindEndpointHorizontalMidpointX(p0, out midStartX, out midStartY, out _);
                        }

                        if (!hasEndRegularBoundaryMid)
                        {
                            hasEndMid = TryFindEndpointHorizontalMidpointX(p1, out midEndX, out midEndY, out _);
                        }

                        if (!hasStartMid)
                        {
                            hasStartMid = TryResolveHorizontalQsecComponentMidpoint(p0, out midStartX, out midStartY);
                        }

                        if (!hasEndMid)
                        {
                            hasEndMid = TryResolveHorizontalQsecComponentMidpoint(p1, out midEndX, out midEndY);
                        }

                        if (p0CorrectionAdjacentForMidpoint)
                        {
                            hasStartMid = false;
                        }

                        if (p1CorrectionAdjacentForMidpoint)
                        {
                            hasEndMid = false;
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

                                if (!TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1))
                                {
                                    continue;
                                }
                            }

                            midpointLockedStart = hasStartMid &&
                                !(IsEndpointNearCorrectionBoundary(p0) && IsEndpointOnThirtyBoundary(p0));
                            midpointLockedEnd = hasEndMid &&
                                !(IsEndpointNearCorrectionBoundary(p1) && IsEndpointOnThirtyBoundary(p1));
                            moveStart = false;
                            moveEnd = false;
                            targetStart = p0;
                            targetEnd = p1;
                        }
                    }
                    else if (IsHorizontalLikeForEndpointEnforcement(p0, p1))
                    {
                        var p0CorrectionAdjacentForMidpoint =
                            IsEndpointNearCorrectionBoundary(p0) && IsEndpointOnThirtyBoundary(p0);
                        var p1CorrectionAdjacentForMidpoint =
                            IsEndpointNearCorrectionBoundary(p1) && IsEndpointOnThirtyBoundary(p1);
                        var hasStartMid = false;
                        var hasEndMid = false;
                        var midStartX = p0.X;
                        var midEndX = p1.X;
                        var midStartY = p0.Y;
                        var midEndY = p1.Y;
                        var midStartHasExplicitX = false;
                        var midEndHasExplicitX = false;
                        var hasStartQsecMidY = false;
                        var hasEndQsecMidY = false;
                        const double qsecMidpointYOverrideTol = 20.0;
                        var hasStartRegularBoundaryMid = false;
                        var hasEndRegularBoundaryMid = false;

                        if (!p0CorrectionAdjacentForMidpoint &&
                            TryFindEndpointRegularVerticalBoundaryMidpoint(
                                p0,
                                out var regularStartX,
                                out var regularStartY,
                                out _))
                        {
                            hasStartMid = true;
                            midStartX = regularStartX;
                            midStartY = regularStartY;
                            midStartHasExplicitX = true;
                            hasStartRegularBoundaryMid = true;
                        }

                        if (!p1CorrectionAdjacentForMidpoint &&
                            TryFindEndpointRegularVerticalBoundaryMidpoint(
                                p1,
                                out var regularEndX,
                                out var regularEndY,
                                out _))
                        {
                            hasEndMid = true;
                            midEndX = regularEndX;
                            midEndY = regularEndY;
                            midEndHasExplicitX = true;
                            hasEndRegularBoundaryMid = true;
                        }

                        if (!hasStartRegularBoundaryMid && !hasEndRegularBoundaryMid &&
                            TryFindPairedHorizontalMidpointY(p0, p1, out var pairedY))
                        {
                            hasStartMid = true;
                            hasEndMid = true;
                            midStartY = pairedY;
                            midEndY = pairedY;
                        }
                        else
                        {
                            if (!hasStartRegularBoundaryMid)
                            {
                                hasStartMid = TryFindEndpointVerticalMidpointY(p0, out midStartY, out _);
                            }

                            if (!hasEndRegularBoundaryMid)
                            {
                                hasEndMid = TryFindEndpointVerticalMidpointY(p1, out midEndY, out _);
                            }
                        }

                        if (p0CorrectionAdjacentForMidpoint)
                        {
                            hasStartMid = false;
                        }

                        if (p1CorrectionAdjacentForMidpoint)
                        {
                            hasEndMid = false;
                        }

                        if (!hasStartRegularBoundaryMid &&
                            !p0CorrectionAdjacentForMidpoint &&
                            TryResolveVerticalQsecComponentMidpoint(p0, out var qsecStartX, out var qsecStartY))
                        {
                            if (!hasStartMid || Math.Abs(qsecStartY - midStartY) <= qsecMidpointYOverrideTol)
                            {
                                hasStartMid = true;
                                midStartY = qsecStartY;
                                hasStartQsecMidY = true;
                                midStartX = qsecStartX;

                                var qsecStartProbe = new Point2d(p0.X, midStartY);
                                if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(qsecStartProbe, p1, midStartY, out var qsecAxisStartX) ||
                                    TryResolveVerticalMidpointAxisX(qsecStartProbe, midStartY, out qsecAxisStartX))
                                {
                                    midStartX = qsecAxisStartX;
                                    midStartHasExplicitX = true;
                                }
                            }
                        }

                        if (!hasEndRegularBoundaryMid &&
                            !p1CorrectionAdjacentForMidpoint &&
                            TryResolveVerticalQsecComponentMidpoint(p1, out var qsecEndX, out var qsecEndY))
                        {
                            if (!hasEndMid || Math.Abs(qsecEndY - midEndY) <= qsecMidpointYOverrideTol)
                            {
                                hasEndMid = true;
                                midEndY = qsecEndY;
                                hasEndQsecMidY = true;
                                midEndX = qsecEndX;

                                var qsecEndProbe = new Point2d(p1.X, midEndY);
                                if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(qsecEndProbe, p0, midEndY, out var qsecAxisEndX) ||
                                    TryResolveVerticalMidpointAxisX(qsecEndProbe, midEndY, out qsecAxisEndX))
                                {
                                    midEndX = qsecAxisEndX;
                                    midEndHasExplicitX = true;
                                }
                            }
                        }

                        // If either side resolved an anchored quarter-line midpoint Y, use it for both
                        // ends so sibling S1/2/N1/2 horizontals land at one shared quarter intersection row.
                        if (!hasStartRegularBoundaryMid &&
                            !hasEndRegularBoundaryMid &&
                            hasStartMid && hasEndMid)
                        {
                            if (hasStartQsecMidY && !hasEndQsecMidY)
                            {
                                midEndY = midStartY;
                            }
                            else if (hasEndQsecMidY && !hasStartQsecMidY)
                            {
                                midStartY = midEndY;
                            }
                            else if (hasStartQsecMidY && hasEndQsecMidY)
                            {
                                const double qsecSharedAverageTol = 4.00;
                                if (Math.Abs(midStartY - midEndY) <= qsecSharedAverageTol)
                                {
                                    var sharedQsecY = 0.5 * (midStartY + midEndY);
                                    midStartY = sharedQsecY;
                                    midEndY = sharedQsecY;
                                }
                            }
                        }

                        // If only one side resolved a midpoint Y, mirror that Y to its sibling endpoint
                        // so both ends can still resolve an explicit quarter-line X intersection.
                        if (!hasStartRegularBoundaryMid &&
                            !hasEndRegularBoundaryMid &&
                            hasStartMid != hasEndMid)
                        {
                            if (hasStartMid)
                            {
                                if (!p1CorrectionAdjacentForMidpoint)
                                {
                                    hasEndMid = true;
                                    midEndY = midStartY;
                                }
                            }
                            else
                            {
                                if (!p0CorrectionAdjacentForMidpoint)
                                {
                                    hasStartMid = true;
                                    midStartY = midEndY;
                                }
                            }
                        }

                        if (hasStartMid && !midStartHasExplicitX)
                        {
                            var startProbe = new Point2d(p0.X, midStartY);
                            if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(startProbe, p1, midStartY, out var axisStartX) ||
                                TryResolveVerticalQsecAxisX(startProbe, out axisStartX) ||
                                TryResolveVerticalMidpointAxisX(startProbe, midStartY, out axisStartX))
                            {
                                midStartX = axisStartX;
                                midStartHasExplicitX = true;
                            }
                        }

                        if (hasEndMid && !midEndHasExplicitX)
                        {
                            var endProbe = new Point2d(p1.X, midEndY);
                            if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(endProbe, p0, midEndY, out var axisEndX) ||
                                TryResolveVerticalQsecAxisX(endProbe, out axisEndX) ||
                                TryResolveVerticalMidpointAxisX(endProbe, midEndY, out axisEndX))
                            {
                                midEndX = axisEndX;
                                midEndHasExplicitX = true;
                            }
                        }

                        if (hasStartMid || hasEndMid)
                        {
                            if (hasStartMid)
                            {
                                var snappedStart = new Point2d(
                                    midStartHasExplicitX ? midStartX : p0.X,
                                    midStartY);
                                if (p0.GetDistanceTo(snappedStart) > midpointEndpointMoveTol)
                                {
                                    moveStart = true;
                                    targetStart = snappedStart;
                                }
                            }

                            if (hasEndMid)
                            {
                                var snappedEnd = new Point2d(
                                    midEndHasExplicitX ? midEndX : p1.X,
                                    midEndY);
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

                                if (!TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1))
                                {
                                    continue;
                                }
                            }

                            // Only lock horizontal endpoints when X is explicitly resolved to the
                            // quarter-line component. Otherwise let the generic hard-boundary snap
                            // finish the X intersection.
                            midpointLockedStart = hasStartMid &&
                                !IsEndpointOnThirtyBoundary(p0) &&
                                midStartHasExplicitX;
                            midpointLockedEnd = hasEndMid &&
                                !IsEndpointOnThirtyBoundary(p1) &&
                                midEndHasExplicitX;
                            moveStart = false;
                            moveEnd = false;
                            targetStart = p0;
                            targetEnd = p1;
                        }
                    }

                    if (midpointLockedStart && midpointLockedEnd)
                    {
                        startDecision = "midpoint-locked";
                        endDecision = "midpoint-locked";
                        if (traceLsdEndpointFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=main skip reason=both-midpoint-locked p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)}.");
                        }

                        continue;
                    }

                    if (!midpointLockedStart)
                    {
                        scannedEndpoints++;
                        var p0OnZero = IsEndpointOnUsecZeroBoundary(p0);
                        var p0OnTwenty = IsEndpointOnUsecTwentyBoundary(p0);
                        var p0OnThirty = IsEndpointOnThirtyBoundary(p0);
                        var p0OnCorrectionOuter = IsEndpointOnCorrectionOuterBoundary(p0);
                        var p0CorrectionAdjacent = IsEndpointNearCorrectionBoundary(p0);
                        // Correction tie-in handling applies to vertical LSD endpoints near correction
                        // seams when they terminate on either 30.18 or the correction outer boundary.
                        var p0OnCorrectionZero = p0OnZero && p0CorrectionAdjacent;
                        var p0CorrectionSnapEligible =
                            IsVerticalLikeForEndpointEnforcement(p0, p1) &&
                            p0CorrectionAdjacent &&
                            !p0OnTwenty &&
                            (p0OnThirty || p0OnCorrectionOuter || p0OnCorrectionZero);
                        if (IsPointOnAnyWindowBoundary(p0, outerBoundaryTol) && !p0OnThirty)
                        {
                            boundarySkipped++;
                            startDecision = "boundary-skip";
                            startSource = "window-boundary";
                        }
                        else
                        {
                            if (p0OnThirty)
                            {
                                onThirtyOnly++;
                            }

                            var snappedStart = p0;
                            var foundStartTarget = false;
                            if (!p0CorrectionSnapEligible &&
                                (p0OnZero || p0OnTwenty) &&
                                TryFindCurrentHardBoundaryMidpoint(
                                    p0,
                                    p1,
                                    p0OnZero ? true : (p0OnTwenty ? false : (bool?)null),
                                    out var currentBoundaryMidStart))
                            {
                                foundStartTarget = true;
                                snappedStart = currentBoundaryMidStart;
                                startSource = "current-hard-midpoint";
                            }

                            if (!foundStartTarget && p0CorrectionSnapEligible)
                            {
                                foundStartTarget = TryFindCorrectionAdjacentSnapTarget(
                                    p0,
                                    p1,
                                    p0OnCorrectionOuter,
                                    out snappedStart);
                                if (foundStartTarget)
                                {
                                    startSource = "correction-adjacent";
                                }
                            }

                            if (!foundStartTarget && p0OnThirty)
                            {
                                bool? preferZero = null;
                                var sourceIsHorizontalLsd = IsHorizontalLikeForEndpointEnforcement(p0, p1);
                                var sourceIsVerticalLsd = IsVerticalLikeForEndpointEnforcement(p0, p1);
                                if (!foundStartTarget && sourceIsHorizontalLsd)
                                {
                                    // Horizontal LSD side rule: right endpoint -> 0, left endpoint -> 20.12.
                                    preferZero = p0.X > p1.X;
                                }
                                else if (!foundStartTarget && sourceIsVerticalLsd)
                                {
                                    // Vertical LSD side rule: north endpoint -> 0, south endpoint -> 20.12.
                                    preferZero = p0.Y > p1.Y;
                                }

                                if (!foundStartTarget)
                                {
                                    if (sourceIsHorizontalLsd || sourceIsVerticalLsd)
                                    {
                                        foundStartTarget = TryFindPreferredHardBoundaryMidpoint(
                                            p0,
                                            p1,
                                            preferZero,
                                            out snappedStart,
                                            maxMoveOverride: thirtyEscapeMaxMove) ||
                                            TryFindPreferredHardBoundaryMidpointRelaxed(
                                                p0,
                                                p1,
                                                preferZero,
                                                out snappedStart,
                                                maxMoveOverride: thirtyEscapeMaxMove) ||
                                            TryFindNearestHardBoundaryPoint(
                                                p0,
                                                p1,
                                                preferZero,
                                                out snappedStart) ||
                                            TryFindNearestHardBoundaryPoint(
                                                p0,
                                                p1,
                                                preferZero,
                                                out snappedStart,
                                                lateralTolOverride: thirtyEscapeLateralTol,
                                                maxMoveOverride: thirtyEscapeMaxMove,
                                                allowBacktrack: true);
                                        if (foundStartTarget)
                                        {
                                            startSource = "thirty-fallback-chain-axis";
                                        }
                                    }
                                    else
                                    {
                                        foundStartTarget =
                                            // Generic fallback for non-axis LSD segments.
                                            TryFindNearestUsecMidpoint(p0, preferZero, out snappedStart) ||
                                            TryFindNearestHardBoundaryPoint(p0, p1, preferZero, out snappedStart) ||
                                            TryFindNearestHardBoundaryPoint(
                                                p0,
                                                p1,
                                                preferZero,
                                                out snappedStart,
                                                lateralTolOverride: thirtyEscapeLateralTol,
                                                maxMoveOverride: thirtyEscapeMaxMove,
                                                allowBacktrack: true);
                                        if (foundStartTarget)
                                        {
                                            startSource = "thirty-fallback-chain-generic";
                                        }
                                    }

                                    if (!foundStartTarget)
                                    {
                                        foundStartTarget = TryFindSnapTarget(p0, p1, out snappedStart);
                                        if (foundStartTarget)
                                        {
                                            startSource = "generic-snaptarget";
                                        }
                                    }
                                }
                            }
                            else if (!foundStartTarget)
                            {
                                foundStartTarget = TryFindSnapTarget(p0, p1, out snappedStart);
                                if (foundStartTarget)
                                {
                                    startSource = "generic-snaptarget";
                                }
                            }
                            if (foundStartTarget)
                            {
                                if (p0.GetDistanceTo(snappedStart) <= endpointTouchTol)
                                {
                                    alreadyOnHardBoundary++;
                                    startDecision = "already-on-target";
                                    if (string.Equals(startSource, "none", StringComparison.OrdinalIgnoreCase))
                                    {
                                        startSource = "resolved-target";
                                    }
                                }
                                else
                                {
                                    moveStart = true;
                                    targetStart = snappedStart;
                                    startDecision = "move-to-target";
                                }
                            }
                            else if (p0OnZero || p0OnTwenty || IsEndpointOnUsecTwentyBoundary(p0))
                            {
                                alreadyOnHardBoundary++;
                                startDecision = "already-on-hard";
                                startSource = "existing-hard";
                            }
                            else if (IsEndpointOnHardBoundary(p0))
                            {
                                alreadyOnHardBoundary++;
                                startDecision = "already-on-hard";
                                startSource = "existing-hard";
                            }
                            else
                            {
                                noTarget++;
                                startDecision = "no-target";
                            }
                        }
                    }
                    else
                    {
                        startDecision = "midpoint-locked";
                        startSource = "midpoint-lock";
                    }

                    if (!midpointLockedEnd)
                    {
                        scannedEndpoints++;
                        var p1OnZero = IsEndpointOnUsecZeroBoundary(p1);
                        var p1OnTwenty = IsEndpointOnUsecTwentyBoundary(p1);
                        var p1OnThirty = IsEndpointOnThirtyBoundary(p1);
                        var p1OnCorrectionOuter = IsEndpointOnCorrectionOuterBoundary(p1);
                        var p1CorrectionAdjacent = IsEndpointNearCorrectionBoundary(p1);
                        var p1OnCorrectionZero = p1OnZero && p1CorrectionAdjacent;
                        var p1CorrectionSnapEligible =
                            IsVerticalLikeForEndpointEnforcement(p0, p1) &&
                            p1CorrectionAdjacent &&
                            !p1OnTwenty &&
                            (p1OnThirty || p1OnCorrectionOuter || p1OnCorrectionZero);
                        if (IsPointOnAnyWindowBoundary(p1, outerBoundaryTol) && !p1OnThirty)
                        {
                            boundarySkipped++;
                            endDecision = "boundary-skip";
                            endSource = "window-boundary";
                        }
                        else
                        {
                            if (p1OnThirty)
                            {
                                onThirtyOnly++;
                            }

                            var snappedEnd = p1;
                            var foundEndTarget = false;
                            if (!p1CorrectionSnapEligible &&
                                (p1OnZero || p1OnTwenty) &&
                                TryFindCurrentHardBoundaryMidpoint(
                                    p1,
                                    p0,
                                    p1OnZero ? true : (p1OnTwenty ? false : (bool?)null),
                                    out var currentBoundaryMidEnd))
                            {
                                foundEndTarget = true;
                                snappedEnd = currentBoundaryMidEnd;
                                endSource = "current-hard-midpoint";
                            }

                            if (!foundEndTarget && p1CorrectionSnapEligible)
                            {
                                foundEndTarget = TryFindCorrectionAdjacentSnapTarget(
                                    p1,
                                    p0,
                                    p1OnCorrectionOuter,
                                    out snappedEnd);
                                if (foundEndTarget)
                                {
                                    endSource = "correction-adjacent";
                                }
                            }

                            if (!foundEndTarget && p1OnThirty)
                            {
                                bool? preferZero = null;
                                var sourceIsHorizontalLsd = IsHorizontalLikeForEndpointEnforcement(p0, p1);
                                var sourceIsVerticalLsd = IsVerticalLikeForEndpointEnforcement(p0, p1);
                                if (!foundEndTarget && sourceIsHorizontalLsd)
                                {
                                    // Horizontal LSD side rule: right endpoint -> 0, left endpoint -> 20.12.
                                    preferZero = p1.X > p0.X;
                                }
                                else if (!foundEndTarget && sourceIsVerticalLsd)
                                {
                                    // Vertical LSD side rule: north endpoint -> 0, south endpoint -> 20.12.
                                    preferZero = p1.Y > p0.Y;
                                }

                                if (!foundEndTarget)
                                {
                                    if (sourceIsHorizontalLsd || sourceIsVerticalLsd)
                                    {
                                        foundEndTarget = TryFindPreferredHardBoundaryMidpoint(
                                            p1,
                                            p0,
                                            preferZero,
                                            out snappedEnd,
                                            maxMoveOverride: thirtyEscapeMaxMove) ||
                                            TryFindPreferredHardBoundaryMidpointRelaxed(
                                                p1,
                                                p0,
                                                preferZero,
                                                out snappedEnd,
                                                maxMoveOverride: thirtyEscapeMaxMove) ||
                                            TryFindNearestHardBoundaryPoint(
                                                p1,
                                                p0,
                                                preferZero,
                                                out snappedEnd) ||
                                            TryFindNearestHardBoundaryPoint(
                                                p1,
                                                p0,
                                                preferZero,
                                                out snappedEnd,
                                                lateralTolOverride: thirtyEscapeLateralTol,
                                                maxMoveOverride: thirtyEscapeMaxMove,
                                                allowBacktrack: true);
                                        if (foundEndTarget)
                                        {
                                            endSource = "thirty-fallback-chain-axis";
                                        }
                                    }
                                    else
                                    {
                                        foundEndTarget =
                                            // Generic fallback for non-axis LSD segments.
                                            TryFindNearestUsecMidpoint(p1, preferZero, out snappedEnd) ||
                                            TryFindNearestHardBoundaryPoint(p1, p0, preferZero, out snappedEnd) ||
                                            TryFindNearestHardBoundaryPoint(
                                                p1,
                                                p0,
                                                preferZero,
                                                out snappedEnd,
                                                lateralTolOverride: thirtyEscapeLateralTol,
                                                maxMoveOverride: thirtyEscapeMaxMove,
                                                allowBacktrack: true);
                                        if (foundEndTarget)
                                        {
                                            endSource = "thirty-fallback-chain-generic";
                                        }
                                    }

                                    if (!foundEndTarget)
                                    {
                                        foundEndTarget = TryFindSnapTarget(p1, p0, out snappedEnd);
                                        if (foundEndTarget)
                                        {
                                            endSource = "generic-snaptarget";
                                        }
                                    }
                                }
                            }
                            else if (!foundEndTarget)
                            {
                                foundEndTarget = TryFindSnapTarget(p1, p0, out snappedEnd);
                                if (foundEndTarget)
                                {
                                    endSource = "generic-snaptarget";
                                }
                            }
                            if (foundEndTarget)
                            {
                                if (p1.GetDistanceTo(snappedEnd) <= endpointTouchTol)
                                {
                                    alreadyOnHardBoundary++;
                                    endDecision = "already-on-target";
                                    if (string.Equals(endSource, "none", StringComparison.OrdinalIgnoreCase))
                                    {
                                        endSource = "resolved-target";
                                    }
                                }
                                else
                                {
                                    moveEnd = true;
                                    targetEnd = snappedEnd;
                                    endDecision = "move-to-target";
                                }
                            }
                            else if (p1OnZero || p1OnTwenty || IsEndpointOnUsecTwentyBoundary(p1))
                            {
                                alreadyOnHardBoundary++;
                                endDecision = "already-on-hard";
                                endSource = "existing-hard";
                            }
                            else if (IsEndpointOnHardBoundary(p1))
                            {
                                alreadyOnHardBoundary++;
                                endDecision = "already-on-hard";
                                endSource = "existing-hard";
                            }
                            else
                            {
                                noTarget++;
                                endDecision = "no-target";
                            }
                        }
                    }
                    else
                    {
                        endDecision = "midpoint-locked";
                        endSource = "midpoint-lock";
                    }

                    if (moveStart && moveEnd && targetStart.GetDistanceTo(targetEnd) < minRemainingLength)
                    {
                        var startMoveDist = p0.GetDistanceTo(targetStart);
                        var endMoveDist = p1.GetDistanceTo(targetEnd);
                        if (startMoveDist >= endMoveDist)
                        {
                            moveEnd = false;
                            endDecision = "move-cancelled-minlength";
                        }
                        else
                        {
                            moveStart = false;
                            startDecision = "move-cancelled-minlength";
                        }
                    }

                    if (!moveStart && !moveEnd)
                    {
                        if (traceLsdEndpointFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=main no-move start={startDecision}({startSource}) end={endDecision}({endSource}) p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)}.");
                        }

                        continue;
                    }

                    var movedLine = false;
                    var movedStart = false;
                    var movedEnd = false;
                    if (moveStart && TryMoveEndpoint(writable, moveStart: true, targetStart, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                        movedStart = true;
                    }
                    else if (moveStart)
                    {
                        startDecision = "move-attempt-failed";
                    }

                    if (moveEnd && TryMoveEndpoint(writable, moveStart: false, targetEnd, endpointMoveTol))
                    {
                        adjustedEndpoints++;
                        movedLine = true;
                        movedEnd = true;
                    }
                    else if (moveEnd)
                    {
                        endDecision = "move-attempt-failed";
                    }

                    if (movedLine)
                    {
                        adjustedLines++;
                    }

                    if (traceLsdEndpointFlow)
                    {
                        if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var postP0, out var postP1))
                        {
                            postP0 = p0;
                            postP1 = p1;
                        }

                        logger?.WriteLine(
                            $"LSD-ENDPT line={lineIdText} pass=main moved={movedLine} movedStart={movedStart} movedEnd={movedEnd} " +
                            $"start={startDecision}({startSource}) end={endDecision}({endSource}) targetStart={FormatLsdEndpointTracePoint(targetStart)} targetEnd={FormatLsdEndpointTracePoint(targetEnd)} " +
                            $"postP0={FormatLsdEndpointTracePoint(postP0)} postP1={FormatLsdEndpointTracePoint(postP1)}.");
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

                        if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
                        {
                            continue;
                        }

                        var movedAny = false;
                        if (IsVerticalLikeForEndpointEnforcement(p0, p1))
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
                        else if (IsHorizontalLikeForEndpointEnforcement(p0, p1))
                        {
                            var moveStart = false;
                            var moveEnd = false;
                            var targetStart = p0;
                            var targetEnd = p1;
                            var traceClampTarget = new Point2d(504123.929, 5986277.757);
                            const double traceClampTol = 140.0;
                            var traceClampThis =
                                p0.GetDistanceTo(traceClampTarget) <= traceClampTol ||
                                p1.GetDistanceTo(traceClampTarget) <= traceClampTol;
                            var p0OnHardForClamp = IsEndpointOnHardBoundary(p0);
                            var p1OnHardForClamp = IsEndpointOnHardBoundary(p1);
                            double? forcedClampY = null;
                            if (traceClampThis && logger != null)
                            {
                                logger.WriteLine(
                                    $"TRACE-LSD-CLAMP start p0=({p0.X:0.###},{p0.Y:0.###}) p1=({p1.X:0.###},{p1.Y:0.###}) p0Hard={p0OnHardForClamp} p1Hard={p1OnHardForClamp}.");
                            }

                            if (TryFindPairedHorizontalMidpointY(p0, p1, out var pairedClampY))
                            {
                                forcedClampY = pairedClampY;
                            }
                            else if (p0OnHardForClamp && !p1OnHardForClamp)
                            {
                                forcedClampY = p0.Y;
                            }
                            else if (p1OnHardForClamp && !p0OnHardForClamp)
                            {
                                forcedClampY = p1.Y;
                            }

                            if (traceClampThis && logger != null)
                            {
                                var forcedText = forcedClampY.HasValue ? forcedClampY.Value.ToString("0.###") : "null";
                                logger.WriteLine($"TRACE-LSD-CLAMP forcedY={forcedText}");
                            }

                            if (!p0OnHardForClamp)
                            {
                                var hasStartComponent = TryFindVerticalQsecComponentMidpoint(p0, out var t0);
                                if (hasStartComponent || forcedClampY.HasValue)
                                {
                                    var clampStartY = forcedClampY ?? t0.Y;
                                    var clampStartX = hasStartComponent ? t0.X : p0.X;
                                    var clampStartProbe = new Point2d(p0.X, clampStartY);
                                    if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(clampStartProbe, p1, clampStartY, out var resolvedStartX) ||
                                        TryResolveVerticalQsecAxisX(clampStartProbe, out resolvedStartX) ||
                                        TryResolveVerticalMidpointAxisX(clampStartProbe, clampStartY, out resolvedStartX))
                                    {
                                        clampStartX = resolvedStartX;
                                    }

                                    var clampedStart = new Point2d(clampStartX, clampStartY);
                                    if (p0.GetDistanceTo(clampedStart) > midpointEndpointMoveTol)
                                    {
                                        moveStart = true;
                                        targetStart = clampedStart;
                                    }

                                    if (traceClampThis && logger != null)
                                    {
                                        logger.WriteLine(
                                            $"TRACE-LSD-CLAMP start-cand hasComp={hasStartComponent} comp=({t0.X:0.###},{t0.Y:0.###}) cand=({clampedStart.X:0.###},{clampedStart.Y:0.###}) moveStart={moveStart}.");
                                    }
                                }
                            }

                            if (!p1OnHardForClamp)
                            {
                                var hasEndComponent = TryFindVerticalQsecComponentMidpoint(p1, out var t1);
                                if (hasEndComponent || forcedClampY.HasValue)
                                {
                                    var clampEndY = forcedClampY ?? t1.Y;
                                    var clampEndX = hasEndComponent ? t1.X : p1.X;
                                    var clampEndProbe = new Point2d(p1.X, clampEndY);
                                    if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(clampEndProbe, p0, clampEndY, out var resolvedEndX) ||
                                        TryResolveVerticalQsecAxisX(clampEndProbe, out resolvedEndX) ||
                                        TryResolveVerticalMidpointAxisX(clampEndProbe, clampEndY, out resolvedEndX))
                                    {
                                        clampEndX = resolvedEndX;
                                    }

                                    var clampedEnd = new Point2d(clampEndX, clampEndY);
                                    if (p1.GetDistanceTo(clampedEnd) > midpointEndpointMoveTol)
                                    {
                                        moveEnd = true;
                                        targetEnd = clampedEnd;
                                    }

                                    if (traceClampThis && logger != null)
                                    {
                                        logger.WriteLine(
                                            $"TRACE-LSD-CLAMP end-cand hasComp={hasEndComponent} comp=({t1.X:0.###},{t1.Y:0.###}) cand=({clampedEnd.X:0.###},{clampedEnd.Y:0.###}) moveEnd={moveEnd}.");
                                    }
                                }
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

                            if (traceClampThis && logger != null)
                            {
                                logger.WriteLine(
                                    $"TRACE-LSD-CLAMP result movedAny={movedAny} targetStart=({targetStart.X:0.###},{targetStart.Y:0.###}) targetEnd=({targetEnd.X:0.###},{targetEnd.Y:0.###}).");
                            }
                        }

                        if (movedAny)
                        {
                            qsecComponentClampLines++;
                            adjustedLines++;
                        }
                    }
                }

                // Final invariant:
                // LSD endpoints must not terminate on/near 30.18. Force them to the
                // preferred 0/20 hard-boundary midpoint (or projected hard-boundary fallback).
                if (lsdLineIds.Count > 0 && hardBoundarySegments.Count > 0 && thirtyBoundarySegments.Count > 0)
                {
                    bool IsEndpointNearThirtyBoundary(Point2d endpoint, double tol)
                    {
                        for (var i = 0; i < thirtyBoundarySegments.Count; i++)
                        {
                            var seg = thirtyBoundarySegments[i];
                            if (DistancePointToSegment(endpoint, seg.A, seg.B) <= tol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    const double nearThirtyTol = 3.00;
                    if (traceLsdEndpointFlow)
                    {
                        logger?.WriteLine(
                            $"LSD-ENDPT final-invariant start lsdLines={lsdLineIds.Count} thirtySegs={thirtyBoundarySegments.Count} nearTol={nearThirtyTol:0.###}.");
                    }

                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        var finalLineIdText = FormatLsdEndpointTraceId(id);

                        if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1) || !IsHorizontalLikeForEndpointEnforcement(p0, p1))
                        {
                            if (!TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1) || !IsVerticalLikeForEndpointEnforcement(p0, p1))
                            {
                                if (traceLsdEndpointFlow)
                                {
                                    logger?.WriteLine(
                                        $"LSD-ENDPT line={finalLineIdText} pass=final-invariant skip reason=non-axis-or-unreadable.");
                                }

                                continue;
                            }

                            var movedVertical = false;
                            var p0NearThirty = IsEndpointNearThirtyBoundary(p0, nearThirtyTol);
                            var p1NearThirty = IsEndpointNearThirtyBoundary(p1, nearThirtyTol);
                            if (traceLsdEndpointFlow)
                            {
                                logger?.WriteLine(
                                    $"LSD-ENDPT line={finalLineIdText} pass=final-invariant orient=V p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)} p0Near30={p0NearThirty} p1Near30={p1NearThirty}.");
                            }

                            if (p0NearThirty)
                            {
                                var preferZero = p0.Y > p1.Y;
                                if (TryFindPreferredHardBoundaryMidpoint(
                                        p0,
                                        p1,
                                        preferZero,
                                        out var target0,
                                        maxMoveOverride: thirtyEscapeMaxMove) ||
                                    TryFindPreferredHardBoundaryMidpointRelaxed(
                                        p0,
                                        p1,
                                        preferZero,
                                        out target0,
                                        maxMoveOverride: thirtyEscapeMaxMove) ||
                                    TryFindNearestHardBoundaryPoint(p0, p1, preferZero, out target0) ||
                                    TryFindNearestHardBoundaryPoint(
                                        p0,
                                        p1,
                                        preferZero,
                                        out target0,
                                        lateralTolOverride: thirtyEscapeLateralTol,
                                        maxMoveOverride: thirtyEscapeMaxMove,
                                        allowBacktrack: true) ||
                                    TryFindNearestHardBoundaryPoint(
                                        p0,
                                        p1,
                                        preferZero: null,
                                        out target0,
                                        lateralTolOverride: thirtyEscapeLateralTol,
                                        maxMoveOverride: thirtyEscapeMaxMove,
                                        allowBacktrack: true))
                                {
                                    if (TryMoveEndpoint(writable, moveStart: true, target0, midpointEndpointMoveTol))
                                    {
                                        adjustedEndpoints++;
                                        movedVertical = true;
                                    }
                                }
                            }

                            if (!TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1))
                            {
                                if (movedVertical)
                                {
                                    adjustedLines++;
                                }

                                continue;
                            }

                            if (IsVerticalLikeForEndpointEnforcement(p0, p1) && IsEndpointNearThirtyBoundary(p1, nearThirtyTol))
                            {
                                var preferZero = p1.Y > p0.Y;
                                if (TryFindPreferredHardBoundaryMidpoint(
                                        p1,
                                        p0,
                                        preferZero,
                                        out var target1,
                                        maxMoveOverride: thirtyEscapeMaxMove) ||
                                    TryFindPreferredHardBoundaryMidpointRelaxed(
                                        p1,
                                        p0,
                                        preferZero,
                                        out target1,
                                        maxMoveOverride: thirtyEscapeMaxMove) ||
                                    TryFindNearestHardBoundaryPoint(p1, p0, preferZero, out target1) ||
                                    TryFindNearestHardBoundaryPoint(
                                        p1,
                                        p0,
                                        preferZero,
                                        out target1,
                                        lateralTolOverride: thirtyEscapeLateralTol,
                                        maxMoveOverride: thirtyEscapeMaxMove,
                                        allowBacktrack: true) ||
                                    TryFindNearestHardBoundaryPoint(
                                        p1,
                                        p0,
                                        preferZero: null,
                                        out target1,
                                        lateralTolOverride: thirtyEscapeLateralTol,
                                        maxMoveOverride: thirtyEscapeMaxMove,
                                        allowBacktrack: true))
                                {
                                    if (TryMoveEndpoint(writable, moveStart: false, target1, midpointEndpointMoveTol))
                                    {
                                        adjustedEndpoints++;
                                        movedVertical = true;
                                    }
                                }
                            }

                            if (movedVertical)
                            {
                                adjustedLines++;
                            }

                            if (traceLsdEndpointFlow)
                            {
                                if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var postV0, out var postV1))
                                {
                                    postV0 = p0;
                                    postV1 = p1;
                                }

                                logger?.WriteLine(
                                    $"LSD-ENDPT line={finalLineIdText} pass=final-invariant orient=V moved={movedVertical} postP0={FormatLsdEndpointTracePoint(postV0)} postP1={FormatLsdEndpointTracePoint(postV1)}.");
                            }

                            continue;
                        }

                        var movedAny = false;
                        var p0NearThirtyH = IsEndpointNearThirtyBoundary(p0, nearThirtyTol);
                        var p1NearThirtyH = IsEndpointNearThirtyBoundary(p1, nearThirtyTol);
                        if (traceLsdEndpointFlow)
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={finalLineIdText} pass=final-invariant orient=H p0={FormatLsdEndpointTracePoint(p0)} p1={FormatLsdEndpointTracePoint(p1)} p0Near30={p0NearThirtyH} p1Near30={p1NearThirtyH}.");
                        }

                        if (p0NearThirtyH)
                        {
                            var preferZero = p0.X > p1.X;
                            if (TryFindPreferredHardBoundaryMidpoint(
                                    p0,
                                    p1,
                                    preferZero,
                                    out var target0,
                                    maxMoveOverride: thirtyEscapeMaxMove) ||
                                TryFindPreferredHardBoundaryMidpointRelaxed(
                                    p0,
                                    p1,
                                    preferZero,
                                    out target0,
                                    maxMoveOverride: thirtyEscapeMaxMove) ||
                                TryFindNearestHardBoundaryPoint(p0, p1, preferZero, out target0) ||
                                TryFindNearestHardBoundaryPoint(
                                    p0,
                                    p1,
                                    preferZero,
                                    out target0,
                                    lateralTolOverride: thirtyEscapeLateralTol,
                                    maxMoveOverride: thirtyEscapeMaxMove,
                                    allowBacktrack: true) ||
                                TryFindNearestHardBoundaryPoint(
                                    p0,
                                    p1,
                                    preferZero: null,
                                    out target0,
                                    lateralTolOverride: thirtyEscapeLateralTol,
                                    maxMoveOverride: thirtyEscapeMaxMove,
                                    allowBacktrack: true))
                            {
                                if (TryMoveEndpoint(writable, moveStart: true, target0, midpointEndpointMoveTol))
                                {
                                    adjustedEndpoints++;
                                    movedAny = true;
                                }
                            }
                        }

                        if (!TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1))
                        {
                            if (movedAny)
                            {
                                adjustedLines++;
                            }

                            continue;
                        }

                        if (IsHorizontalLikeForEndpointEnforcement(p0, p1) && p1NearThirtyH)
                        {
                            var preferZero = p1.X > p0.X;
                            if (TryFindPreferredHardBoundaryMidpoint(
                                    p1,
                                    p0,
                                    preferZero,
                                    out var target1,
                                    maxMoveOverride: thirtyEscapeMaxMove) ||
                                TryFindPreferredHardBoundaryMidpointRelaxed(
                                    p1,
                                    p0,
                                    preferZero,
                                    out target1,
                                    maxMoveOverride: thirtyEscapeMaxMove) ||
                                TryFindNearestHardBoundaryPoint(p1, p0, preferZero, out target1) ||
                                TryFindNearestHardBoundaryPoint(
                                    p1,
                                    p0,
                                    preferZero,
                                    out target1,
                                    lateralTolOverride: thirtyEscapeLateralTol,
                                    maxMoveOverride: thirtyEscapeMaxMove,
                                    allowBacktrack: true) ||
                                TryFindNearestHardBoundaryPoint(
                                    p1,
                                    p0,
                                    preferZero: null,
                                    out target1,
                                    lateralTolOverride: thirtyEscapeLateralTol,
                                    maxMoveOverride: thirtyEscapeMaxMove,
                                    allowBacktrack: true))
                            {
                                if (TryMoveEndpoint(writable, moveStart: false, target1, midpointEndpointMoveTol))
                                {
                                    adjustedEndpoints++;
                                    movedAny = true;
                                }
                            }
                        }

                        if (movedAny)
                        {
                            adjustedLines++;
                        }

                        if (traceLsdEndpointFlow)
                        {
                            if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var postH0, out var postH1))
                            {
                                postH0 = p0;
                                postH1 = p1;
                            }

                            logger?.WriteLine(
                                $"LSD-ENDPT line={finalLineIdText} pass=final-invariant orient=H moved={movedAny} postP0={FormatLsdEndpointTracePoint(postH0)} postP1={FormatLsdEndpointTracePoint(postH1)}.");
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

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForEndpointEnforcement(p, tol, clipWindows);

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);


            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

            bool IsBlindSourceLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
            }

            // Accept both canonical and alias names used in old files/log notes.
            bool IsHardBoundaryLayer(string layer) =>
                IsHardBoundaryLayerForEndpointEnforcement(layer, includeSecAliases: true);

            bool IsThirtyEighteenLayer(string layer) => IsThirtyEighteenLayerForEndpointEnforcement(layer);

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

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b))
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

                bool IsEndpointOnHardBoundary(Point2d endpoint) =>
                    IsEndpointOnBoundarySegmentsForEndpointEnforcement(endpoint, hardBoundarySegments, endpointTouchTol);

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

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
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

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);




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

                    var layerName = ent.Layer ?? string.Empty;
                    var isUsec = IsUsecLayer(layerName);
                    var isSec = string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b))
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

                    if (!IsHorizontalLikeForEndpointEnforcement(a, b) && !IsVerticalLikeForEndpointEnforcement(a, b))
                    {
                        continue;
                    }

                    candidates.Add((id, layerName, a, b, Midpoint(a, b), len));
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

        private static bool IsPointInAnyWindowForEndpointEnforcement(Point2d point, IReadOnlyList<Extents3d> clipWindows)
        {
            for (var i = 0; i < clipWindows.Count; i++)
            {
                var window = clipWindows[i];
                if (point.X >= window.MinPoint.X &&
                    point.X <= window.MaxPoint.X &&
                    point.Y >= window.MinPoint.Y &&
                    point.Y <= window.MaxPoint.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPointOnAnyWindowBoundaryForEndpointEnforcement(
            Point2d point,
            double tolerance,
            IReadOnlyList<Extents3d> clipWindows)
        {
            for (var i = 0; i < clipWindows.Count; i++)
            {
                var window = clipWindows[i];
                var withinX = point.X >= (window.MinPoint.X - tolerance) && point.X <= (window.MaxPoint.X + tolerance);
                var withinY = point.Y >= (window.MinPoint.Y - tolerance) && point.Y <= (window.MaxPoint.Y + tolerance);
                if (!withinX || !withinY)
                {
                    continue;
                }

                var onLeft = Math.Abs(point.X - window.MinPoint.X) <= tolerance;
                var onRight = Math.Abs(point.X - window.MaxPoint.X) <= tolerance;
                var onBottom = Math.Abs(point.Y - window.MinPoint.Y) <= tolerance;
                var onTop = Math.Abs(point.Y - window.MaxPoint.Y) <= tolerance;
                if (onLeft || onRight || onBottom || onTop)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DoesSegmentIntersectAnyWindowForEndpointEnforcement(Point2d a, Point2d b, IReadOnlyList<Extents3d> clipWindows)
        {
            if (IsPointInAnyWindowForEndpointEnforcement(a, clipWindows) ||
                IsPointInAnyWindowForEndpointEnforcement(b, clipWindows))
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

        private static bool TryMoveEndpointForEndpointEnforcement(Entity writable, bool moveStart, Point2d target, double moveTolerance)
        {
            if (writable is Line ln)
            {
                var old = moveStart
                    ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                    : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                if (old.GetDistanceTo(target) <= moveTolerance)
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
                var endpointIndex = moveStart ? 0 : pl.NumberOfVertices - 1;
                var old = pl.GetPoint2dAt(endpointIndex);
                if (old.GetDistanceTo(target) <= moveTolerance)
                {
                    return false;
                }

                pl.SetPointAt(endpointIndex, target);
                return true;
            }

            return false;
        }

        private static bool IsHardBoundaryLayerForEndpointEnforcement(string layer, bool includeSecAliases)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            if (string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return includeSecAliases &&
                   (string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsThirtyEighteenLayerForEndpointEnforcement(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEndpointNearBoundarySegmentsForEndpointEnforcement(
            Point2d endpoint,
            IReadOnlyList<(Point2d A, Point2d B)> boundarySegments,
            double touchTolerance)
        {
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var segment = boundarySegments[i];
                if (DistancePointToSegment(endpoint, segment.A, segment.B) <= touchTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEndpointOnBoundarySegmentsForEndpointEnforcement(
            Point2d endpoint,
            IReadOnlyList<(Point2d A, Point2d B)> boundarySegments,
            double touchTolerance)
        {
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var segment = boundarySegments[i];
                if (DistancePointToSegment(endpoint, segment.A, segment.B) <= touchTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEndpointOnBoundarySegmentsForEndpointEnforcement(
            Point2d endpoint,
            IReadOnlyList<(Point2d A, Point2d B, bool IsZero)> boundarySegments,
            double touchTolerance)
        {
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var segment = boundarySegments[i];
                if (DistancePointToSegment(endpoint, segment.A, segment.B) <= touchTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEndpointOnBoundarySegmentsForEndpointEnforcement(
            Point2d endpoint,
            ObjectId sourceId,
            IReadOnlyList<(ObjectId Id, Point2d A, Point2d B)> boundarySegments,
            double touchTolerance)
        {
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var segment = boundarySegments[i];
                if (segment.Id == sourceId)
                {
                    continue;
                }

                if (DistancePointToSegment(endpoint, segment.A, segment.B) <= touchTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static Point2d ClosestPointOnSegmentForEndpointEnforcement(Point2d point, Point2d segmentStart, Point2d segmentEnd)
        {
            var segment = segmentEnd - segmentStart;
            var lengthSquared = segment.DotProduct(segment);
            if (lengthSquared <= 1e-12)
            {
                return segmentStart;
            }

            var offset = point - segmentStart;
            var t = offset.DotProduct(segment) / lengthSquared;
            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            return new Point2d(
                segmentStart.X + (segment.X * t),
                segmentStart.Y + (segment.Y * t));
        }

        private static bool TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
            Point2d endpoint,
            Point2d segmentStart,
            Point2d segmentEnd,
            Vector2d stationAxisUnit,
            double stationTolerance,
            out Point2d target)
        {
            target = default;
            var station = (endpoint.X * stationAxisUnit.X) + (endpoint.Y * stationAxisUnit.Y);
            if (SegmentStationProjection.TryResolvePointAtStation(
                    new ProjectedStationPoint(segmentStart.X, segmentStart.Y),
                    new ProjectedStationPoint(segmentEnd.X, segmentEnd.Y),
                    new ProjectedStationVector(stationAxisUnit.X, stationAxisUnit.Y),
                    station,
                    stationTolerance,
                    out var projected))
            {
                target = new Point2d(projected.X, projected.Y);
                return true;
            }

            var closest = ClosestPointOnSegmentForEndpointEnforcement(endpoint, segmentStart, segmentEnd);
            var closestStation = (closest.X * stationAxisUnit.X) + (closest.Y * stationAxisUnit.Y);
            if (Math.Abs(closestStation - station) > stationTolerance)
            {
                return false;
            }

            target = closest;
            return true;
        }

        private static bool TryReadOpenSegmentForEndpointEnforcement(Entity ent, out Point2d a, out Point2d b)
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

        private static bool IsHorizontalLikeForEndpointEnforcement(Point2d a, Point2d b)
        {
            var d = b - a;
            return Math.Abs(d.X) >= Math.Abs(d.Y);
        }

        private static bool IsVerticalLikeForEndpointEnforcement(Point2d a, Point2d b)
        {
            var d = b - a;
            return Math.Abs(d.Y) > Math.Abs(d.X);
        }

        private static bool IsHorizontalLikeForEndpointEnforcement(
            Point2d a,
            Point2d b,
            Vector2d eastUnit,
            Vector2d northUnit)
        {
            var d = b - a;
            var eastSpan = Math.Abs(d.DotProduct(eastUnit));
            var northSpan = Math.Abs(d.DotProduct(northUnit));
            return eastSpan >= northSpan;
        }

        private static bool IsVerticalLikeForEndpointEnforcement(
            Point2d a,
            Point2d b,
            Vector2d eastUnit,
            Vector2d northUnit)
        {
            var d = b - a;
            var eastSpan = Math.Abs(d.DotProduct(eastUnit));
            var northSpan = Math.Abs(d.DotProduct(northUnit));
            return northSpan > eastSpan;
        }

        private static string FormatLsdEndpointTracePoint(Point2d point)
        {
            return $"({point.X:0.###},{point.Y:0.###})";
        }

        private static string FormatLsdEndpointTraceId(ObjectId objectId)
        {
            try
            {
                return objectId.Handle.ToString();
            }
            catch
            {
                return objectId.ToString();
            }
        }
    }
}





