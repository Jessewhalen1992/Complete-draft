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
        private readonly record struct LsdQuarterContext(
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
            Point2d RightAnchor);

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

            bool IsSecSourceLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsUsec2012Layer(string layer)
            {
                return string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsRegularUsecBoundaryLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsSecLikeHardTarget(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsHardBoundaryLayer(string layer) =>
                IsHardBoundaryLayerForEndpointEnforcement(layer, includeSecAliases: false);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var hardBoundarySegments = new List<(ObjectId Id, Point2d A, Point2d B, string Layer)>();
                var regularUsecBoundarySegments = new List<(ObjectId Id, Point2d A, Point2d B, bool IsHorizontal, bool IsVertical)>();
                var secSourceIds = new List<ObjectId>();
                var sectionSplitVerticalSegments = new List<(ObjectId Id, Point2d A, Point2d B, string Layer)>();
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
                        hardBoundarySegments.Add((id, a, b, layer));
                    }

                    if (IsRegularUsecBoundaryLayer(layer))
                    {
                        regularUsecBoundarySegments.Add((
                            id,
                            a,
                            b,
                            IsHorizontalLikeForEndpointEnforcement(a, b),
                            IsVerticalLikeForEndpointEnforcement(a, b)));
                    }

                    if (IsVerticalLikeForEndpointEnforcement(a, b) &&
                        (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase)))
                    {
                        sectionSplitVerticalSegments.Add((id, a, b, layer));
                    }

                    if (IsSecSourceLayer(layer) &&
                        !IsHorizontalLikeForEndpointEnforcement(a, b))
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
                const double maxWindowBoundaryInsetMove = CorrectionLinePostInsetMeters + 1.0;
                const double maxWindowBoundaryUsec2012Move = RoadAllowanceSecWidthMeters + 1.0;
                const double maxNonSecHardExtend = CorrectionLinePostInsetMeters + 1.0;

                var scannedEndpoints = 0;
                var alreadyOnHard = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;
                var fallbackEndpointUsed = 0;
                var moveSamples = new List<string>();

                bool IsEndpointOnHardBoundary(Point2d endpoint, ObjectId sourceId)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId)
                        {
                            continue;
                        }

                        // A correction-zero inset is not the final authoritative stop for
                        // a vertical L-SEC endpoint if a true correction outer is available
                        // farther along the same axis.
                        if (string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
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

                bool EndpointTouchesCorrectionOuterBoundary(Point2d endpoint, ObjectId sourceId)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId ||
                            !string.Equals(seg.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
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

                bool TryFindCorrectionOuterCompanionExtension(
                    Point2d endpoint,
                    Point2d other,
                    ObjectId sourceId,
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
                    const double maxCorrectionOuterCompanionExtend = CorrectionLinePostInsetMeters + 1.5;
                    const double correctionOuterCompanionAxisTol = 0.80;
                    const double correctionOuterCompanionEndpointTol = 0.35;
                    const double correctionOuterCompanionMatchTol = 0.20;
                    const double correctionOuterCompanionDirectionDotMin = 0.985;
                    var found = false;
                    var bestT = double.MaxValue;
                    var bestEndpointDistance = double.MaxValue;
                    var bestTarget = endpoint;

                    void ConsiderCandidate(Point2d candidatePoint, double endpointDistance)
                    {
                        if (DistancePointToInfiniteLine(candidatePoint, endpoint, endpoint + outwardDir) > correctionOuterCompanionAxisTol)
                        {
                            return;
                        }

                        var t = (candidatePoint - endpoint).DotProduct(outwardDir);
                        if (t <= minExtend || t > maxCorrectionOuterCompanionExtend)
                        {
                            return;
                        }

                        var projectedFromOther = (candidatePoint - other).DotProduct(outwardDir);
                        if (projectedFromOther < minRemainingLength)
                        {
                            return;
                        }

                        if (!found ||
                            t < bestT - 1e-6 ||
                            (Math.Abs(t - bestT) <= 1e-6 && endpointDistance < bestEndpointDistance - 1e-6))
                        {
                            found = true;
                            bestT = t;
                            bestEndpointDistance = endpointDistance;
                            bestTarget = candidatePoint;
                        }
                    }

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId ||
                            !string.Equals(seg.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                            !IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B) ||
                            DistancePointToSegment(endpoint, seg.A, seg.B) > endpointTouchTol)
                        {
                            continue;
                        }

                        var anchorEndpoint = endpoint.GetDistanceTo(seg.A) <= endpoint.GetDistanceTo(seg.B) ? seg.A : seg.B;
                        if (endpoint.GetDistanceTo(anchorEndpoint) > correctionOuterCompanionEndpointTol)
                        {
                            continue;
                        }

                        var anchorDir = seg.B - seg.A;
                        var anchorLen = anchorDir.Length;
                        if (anchorLen <= 1e-6)
                        {
                            continue;
                        }

                        var normal = new Vector2d(anchorDir.Y / anchorLen, -anchorDir.X / anchorLen);
                        var candidateTargets = new[]
                        {
                            anchorEndpoint + (normal * CorrectionLinePostInsetMeters),
                            anchorEndpoint - (normal * CorrectionLinePostInsetMeters)
                        };

                        for (var j = 0; j < hardBoundarySegments.Count; j++)
                        {
                            var candidateSeg = hardBoundarySegments[j];
                            if (candidateSeg.Id == sourceId ||
                                candidateSeg.Id == seg.Id ||
                                !string.Equals(candidateSeg.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                                !IsHorizontalLikeForEndpointEnforcement(candidateSeg.A, candidateSeg.B))
                            {
                                continue;
                            }

                            var candidateDir = candidateSeg.B - candidateSeg.A;
                            var candidateLen = candidateDir.Length;
                            if (candidateLen <= 1e-6 ||
                                Math.Abs((candidateDir / candidateLen).DotProduct(anchorDir / anchorLen)) < correctionOuterCompanionDirectionDotMin)
                            {
                                continue;
                            }

                            var candidateEndpoints = new[] { candidateSeg.A, candidateSeg.B };
                            for (var endpointIndex = 0; endpointIndex < candidateEndpoints.Length; endpointIndex++)
                            {
                                var candidateEndpoint = candidateEndpoints[endpointIndex];
                                for (var targetIndex = 0; targetIndex < candidateTargets.Length; targetIndex++)
                                {
                                    var exactCompanionTarget = candidateTargets[targetIndex];
                                    var endpointDistance = candidateEndpoint.GetDistanceTo(exactCompanionTarget);
                                    if (endpointDistance > correctionOuterCompanionMatchTol)
                                    {
                                        continue;
                                    }

                                    ConsiderCandidate(exactCompanionTarget, endpointDistance);
                                }
                            }
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = bestTarget;
                    return true;
                }

                bool EndpointTouchesSectionSplitVertical(Point2d endpoint, ObjectId sourceId)
                {
                    for (var i = 0; i < sectionSplitVerticalSegments.Count; i++)
                    {
                        var seg = sectionSplitVerticalSegments[i];
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

                bool HasMatchingCorrectionZeroSpan(Point2d a, Point2d b)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var direct =
                            seg.A.GetDistanceTo(a) <= 0.20 &&
                            seg.B.GetDistanceTo(b) <= 0.20;
                        var reverse =
                            seg.A.GetDistanceTo(b) <= 0.20 &&
                            seg.B.GetDistanceTo(a) <= 0.20;
                        if (direct || reverse)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasMatchingCorrectionOuterSpan(Point2d a, Point2d b)
                {
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!string.Equals(seg.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var direct =
                            seg.A.GetDistanceTo(a) <= 0.20 &&
                            seg.B.GetDistanceTo(b) <= 0.20;
                        var reverse =
                            seg.A.GetDistanceTo(b) <= 0.20 &&
                            seg.B.GetDistanceTo(a) <= 0.20;
                        if (direct || reverse)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasCorrectionOuterCoverageForZeroSpan(Point2d zeroA, Point2d zeroB)
                {
                    var dir = zeroB - zeroA;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!string.Equals(seg.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                            !IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B))
                        {
                            continue;
                        }

                        var segDir = seg.B - seg.A;
                        var segLen = segDir.Length;
                        if (segLen <= 1e-6)
                        {
                            continue;
                        }

                        var segU = segDir / segLen;
                        if (Math.Abs(segU.DotProduct(u)) < 0.985)
                        {
                            continue;
                        }

                        var overlapMin = Math.Max(Math.Min(zeroA.X, zeroB.X), Math.Min(seg.A.X, seg.B.X));
                        var overlapMax = Math.Min(Math.Max(zeroA.X, zeroB.X), Math.Max(seg.A.X, seg.B.X));
                        if (overlapMax - overlapMin < len - 1.0)
                        {
                            continue;
                        }

                        var signedOffset = Math.Abs(DistancePointToInfiniteLine(Midpoint(zeroA, zeroB), seg.A, seg.B));
                        if (Math.Abs(signedOffset - CorrectionLinePostInsetMeters) > 0.25)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool TryEnsureLocalCorrectionOuterOverlapFromZeroSpan(
                    Point2d zeroAnchor,
                    Point2d zeroIntersection)
                {
                    const double directionDotMin = 0.985;
                    const double offsetTol = 0.25;
                    const double anchorProjectionTol = 5.0;
                    const double minForwardExtension = 20.0;
                    const double maxForwardExtension = 250.0;

                    var zeroDir = zeroIntersection - zeroAnchor;
                    var zeroLen = zeroDir.Length;
                    if (zeroLen <= 1e-6)
                    {
                        return false;
                    }

                    var zeroU = zeroDir / zeroLen;
                    var zeroMid = Midpoint(zeroAnchor, zeroIntersection);
                    var found = false;
                    var bestForwardExtension = double.MaxValue;
                    var bestProjectedFar = default(Point2d);

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!string.Equals(seg.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                            !IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B))
                        {
                            continue;
                        }

                        var segDir = seg.B - seg.A;
                        var segLen = segDir.Length;
                        if (segLen <= 1e-6)
                        {
                            continue;
                        }

                        var segU = segDir / segLen;
                        if (Math.Abs(segU.DotProduct(zeroU)) < directionDotMin)
                        {
                            continue;
                        }

                        var signedOffset = Math.Abs(DistancePointToInfiniteLine(zeroMid, seg.A, seg.B));
                        if (Math.Abs(signedOffset - CorrectionLinePostInsetMeters) > offsetTol)
                        {
                            continue;
                        }

                        var tA = (seg.A - zeroAnchor).DotProduct(zeroU);
                        var tB = (seg.B - zeroAnchor).DotProduct(zeroU);
                        var nearT = Math.Min(tA, tB);
                        var farT = Math.Max(tA, tB);
                        if (Math.Abs(nearT) > anchorProjectionTol)
                        {
                            continue;
                        }

                        var forwardExtension = farT - zeroLen;
                        if (forwardExtension <= minForwardExtension || forwardExtension > maxForwardExtension)
                        {
                            continue;
                        }

                        var projectedFar = zeroAnchor + (zeroU * farT);
                        if (HasMatchingCorrectionOuterSpan(zeroIntersection, projectedFar))
                        {
                            continue;
                        }

                        if (!found || forwardExtension < bestForwardExtension - 1e-6)
                        {
                            found = true;
                            bestForwardExtension = forwardExtension;
                            bestProjectedFar = projectedFar;
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    var modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var line = new Line(
                        new Point3d(zeroIntersection.X, zeroIntersection.Y, 0.0),
                        new Point3d(bestProjectedFar.X, bestProjectedFar.Y, 0.0))
                    {
                        Layer = LayerUsecCorrection,
                        ColorIndex = 256
                    };
                    var newId = modelSpace.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                    hardBoundarySegments.Add((newId, zeroIntersection, bestProjectedFar, LayerUsecCorrection));
                    return true;
                }

                bool TryEnsureLocalCorrectionZeroOverlapFromMovedVerticalEndpoint(
                    Point2d movedEndpoint,
                    Point2d otherEndpoint,
                    ObjectId sourceId)
                {
                    const double overlapProjectionGapMin = 1.0;
                    const double overlapProjectionGapMax = CorrectionLinePostInsetMeters * 2.5;
                    const double minAnchorSideExtension = 20.0;
                    const double maxAnchorSideExtension = 1200.0;

                    Point2d? bestAnchor = null;
                    Point2d bestIntersection = default;
                    var bestMove = double.MaxValue;

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (!string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                            !IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B))
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLinesForPluginGeometry(movedEndpoint, otherEndpoint, seg.A, seg.B, out var intersection))
                        {
                            continue;
                        }

                        var endpointGap = movedEndpoint.GetDistanceTo(intersection);
                        if (endpointGap < overlapProjectionGapMin || endpointGap > overlapProjectionGapMax)
                        {
                            continue;
                        }

                        Point2d? anchor = null;
                        Point2d opposite = default;
                        if (EndpointTouchesSectionSplitVertical(seg.A, sourceId))
                        {
                            anchor = seg.A;
                            opposite = seg.B;
                        }
                        else if (EndpointTouchesSectionSplitVertical(seg.B, sourceId))
                        {
                            anchor = seg.B;
                            opposite = seg.A;
                        }

                        if (anchor == null)
                        {
                            continue;
                        }

                        var outwardFromInterior = anchor.Value - opposite;
                        var outwardFromInteriorLen = outwardFromInterior.Length;
                        if (outwardFromInteriorLen <= 1e-6)
                        {
                            continue;
                        }

                        var outwardFromInteriorDir = outwardFromInterior / outwardFromInteriorLen;
                        var anchorSideExtension = (intersection - anchor.Value).DotProduct(outwardFromInteriorDir);
                        if (anchorSideExtension <= minAnchorSideExtension || anchorSideExtension > maxAnchorSideExtension)
                        {
                            continue;
                        }

                        if (HasMatchingCorrectionZeroSpan(anchor.Value, intersection) ||
                            !HasCorrectionOuterCoverageForZeroSpan(anchor.Value, intersection))
                        {
                            continue;
                        }

                        if (endpointGap < bestMove)
                        {
                            bestMove = endpointGap;
                            bestAnchor = anchor;
                            bestIntersection = intersection;
                        }
                    }

                    if (bestAnchor == null)
                    {
                        return false;
                    }

                    var modelSpace = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var line = new Line(
                        new Point3d(bestAnchor.Value.X, bestAnchor.Value.Y, 0.0),
                        new Point3d(bestIntersection.X, bestIntersection.Y, 0.0))
                    {
                        Layer = LayerUsecCorrectionZero,
                        ColorIndex = 256
                    };
                    var newId = modelSpace.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                    hardBoundarySegments.Add((newId, bestAnchor.Value, bestIntersection, LayerUsecCorrectionZero));
                    TryEnsureLocalCorrectionOuterOverlapFromZeroSpan(bestAnchor.Value, bestIntersection);
                    return true;
                }

                bool TryFindCorrectionZeroCompanionExtension(
                    Point2d endpoint,
                    Point2d other,
                    ObjectId sourceId,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!EndpointTouchesCorrectionOuterBoundary(endpoint, sourceId))
                    {
                        return false;
                    }

                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    const double maxCorrectionZeroCompanionExtend = CorrectionLinePostInsetMeters + 1.5;
                    var found = false;
                    var bestT = double.MaxValue;
                    var bestEndpointDistance = double.MaxValue;
                    var bestTarget = endpoint;

                    void ConsiderCandidate(Point2d candidatePoint, double t, double endpointDistance)
                    {
                        if (t <= minExtend || t > maxCorrectionZeroCompanionExtend)
                        {
                            return;
                        }

                        if (!found ||
                            t < bestT - 1e-6 ||
                            (Math.Abs(t - bestT) <= 1e-6 && endpointDistance < bestEndpointDistance - 1e-6))
                        {
                            found = true;
                            bestT = t;
                            bestEndpointDistance = endpointDistance;
                            bestTarget = candidatePoint;
                        }
                    }

                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        if (seg.Id == sourceId ||
                            !string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t))
                        {
                            var candidatePoint = endpoint + (outwardDir * t);
                            var endpointDistance = Math.Min(
                                candidatePoint.GetDistanceTo(seg.A),
                                candidatePoint.GetDistanceTo(seg.B));
                            ConsiderCandidate(candidatePoint, t, endpointDistance);
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var candidatePoint = endpointIndex == 0 ? seg.A : seg.B;
                            if (DistancePointToInfiniteLine(candidatePoint, endpoint, endpoint + outwardDir) > endpointAxisTol)
                            {
                                continue;
                            }

                            var endpointT = (candidatePoint - endpoint).DotProduct(outwardDir);
                            var endpointDistance = Math.Min(
                                candidatePoint.GetDistanceTo(seg.A),
                                candidatePoint.GetDistanceTo(seg.B));
                            ConsiderCandidate(candidatePoint, endpointT, endpointDistance);
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = bestTarget;
                    return true;
                }

                bool TryFindRegularUsecCorridorTarget(
                    Point2d endpoint,
                    Point2d other,
                    ObjectId sourceId,
                    out Point2d target)
                {
                    target = endpoint;
                    var sourceLen = endpoint.GetDistanceTo(other);
                    const double minCorridorRetargetSourceLength = 100.0;
                    if (sourceLen < minCorridorRetargetSourceLength)
                    {
                        return false;
                    }

                    var endpointOnRegularUsecBoundary = false;
                    for (var i = 0; i < regularUsecBoundarySegments.Count; i++)
                    {
                        var seg = regularUsecBoundarySegments[i];
                        if (seg.Id == sourceId)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            endpointOnRegularUsecBoundary = true;
                            break;
                        }
                    }

                    if (!endpointOnRegularUsecBoundary)
                    {
                        return false;
                    }

                    var sourceIsVertical = IsVerticalLikeForEndpointEnforcement(endpoint, other);
                    var sourceIsHorizontal = IsHorizontalLikeForEndpointEnforcement(endpoint, other);
                    if (!sourceIsVertical && !sourceIsHorizontal)
                    {
                        return false;
                    }

                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    const double ordinaryCorridorTol = 2.0;
                    var ordinaryFound = false;
                    var bestOrdinaryDistanceDelta = double.MaxValue;
                    var bestOrdinaryT = double.MaxValue;
                    var bestOrdinaryTarget = endpoint;
                    void ConsiderOrdinaryCandidate(Point2d candidatePoint, double t)
                    {
                        if (t <= minExtend || t > maxExtend)
                        {
                            return;
                        }

                        var corridorDelta = Math.Abs(t - RoadAllowanceSecWidthMeters);
                        if (corridorDelta > ordinaryCorridorTol)
                        {
                            return;
                        }

                        var projectedFromOther = (candidatePoint - other).DotProduct(outwardDir);
                        if (projectedFromOther < minRemainingLength)
                        {
                            return;
                        }

                        if (!ordinaryFound ||
                            corridorDelta < bestOrdinaryDistanceDelta - 1e-6 ||
                            (Math.Abs(corridorDelta - bestOrdinaryDistanceDelta) <= 1e-6 && t < bestOrdinaryT))
                        {
                            ordinaryFound = true;
                            bestOrdinaryDistanceDelta = corridorDelta;
                            bestOrdinaryT = t;
                            bestOrdinaryTarget = candidatePoint;
                        }
                    }

                    for (var i = 0; i < regularUsecBoundarySegments.Count; i++)
                    {
                        var seg = regularUsecBoundarySegments[i];
                        if (seg.Id == sourceId ||
                            DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTouchTol)
                        {
                            continue;
                        }

                        if (sourceIsVertical && !seg.IsHorizontal)
                        {
                            continue;
                        }

                        if (sourceIsHorizontal && !seg.IsVertical)
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t))
                        {
                            for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                            {
                                var candidate = endpointIndex == 0 ? seg.A : seg.B;
                                if (DistancePointToInfiniteLine(candidate, endpoint, endpoint + outwardDir) > endpointAxisTol)
                                {
                                    continue;
                                }

                                var endpointT = (candidate - endpoint).DotProduct(outwardDir);
                                ConsiderOrdinaryCandidate(candidate, endpointT);
                            }

                            continue;
                        }

                        ConsiderOrdinaryCandidate(endpoint + (outwardDir * t), t);
                    }

                    if (!ordinaryFound)
                    {
                        return false;
                    }

                    target = bestOrdinaryTarget;
                    return true;
                }

                bool TryFindExtensionTarget(
                    Point2d endpoint,
                    Point2d other,
                    ObjectId sourceId,
                    out Point2d target,
                    out bool allowWindowBoundaryUsec2012Move)
                {
                    target = endpoint;
                    allowWindowBoundaryUsec2012Move = false;
                    var outward = endpoint - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var found = false;
                    var usedFallback = false;
                    var usedUsec2012Target = false;
                    var bestT = double.MaxValue;
                    var sourceIsVertical = IsVerticalLikeForEndpointEnforcement(endpoint, other);
                    var sourceIsHorizontal = IsHorizontalLikeForEndpointEnforcement(endpoint, other);
                    void ConsiderCandidate(double t, string targetLayer, bool isFallback, bool isUsec2012Target)
                    {
                        if (t <= minExtend || t > maxExtend)
                        {
                            return;
                        }

                        var isSecLikeTarget = IsSecLikeHardTarget(targetLayer);
                        if (!isSecLikeTarget && !isUsec2012Target && t > maxNonSecHardExtend)
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
                        usedUsec2012Target = isUsec2012Target;
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

                        ConsiderCandidate(t, seg.Layer, isFallback: false, isUsec2012Target: IsUsec2012Layer(seg.Layer));
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
                            ConsiderCandidate(t, seg.Layer, isFallback: true, isUsec2012Target: IsUsec2012Layer(seg.Layer));
                        }
                    }

                    if (TryFindRegularUsecCorridorTarget(endpoint, other, sourceId, out var regularUsecTarget))
                    {
                        target = regularUsecTarget;
                        return true;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    if (usedFallback)
                    {
                        fallbackEndpointUsed++;
                    }

                    if (usedUsec2012Target)
                    {
                        allowWindowBoundaryUsec2012Move = true;
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

                    var movePlan = EndpointMovePlan.Create(p0, p1);
                    var startUsedCorrectionOuterCompanion = false;
                    var endUsedCorrectionOuterCompanion = false;

                    scannedEndpoints++;
                    var p0OnWindowBoundary = IsPointOnAnyWindowBoundary(p0, outerBoundaryTol);
                    if (TryFindRegularUsecCorridorTarget(
                            p0,
                            p1,
                            sourceId,
                            out var corridorStart))
                    {
                        var maxBoundaryMove = maxWindowBoundaryUsec2012Move;
                        if (p0OnWindowBoundary && p0.GetDistanceTo(corridorStart) > maxBoundaryMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveStart = true;
                            movePlan.TargetStart = corridorStart;
                        }
                    }
                    else if (TryFindCorrectionOuterCompanionExtension(
                                 p0,
                                 p1,
                                 sourceId,
                                 out var correctionOuterCompanionStart))
                    {
                        if (p0OnWindowBoundary && p0.GetDistanceTo(correctionOuterCompanionStart) > maxWindowBoundaryInsetMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveStart = true;
                            movePlan.TargetStart = correctionOuterCompanionStart;
                            startUsedCorrectionOuterCompanion = true;
                        }
                    }
                    else if (TryFindCorrectionZeroCompanionExtension(
                                 p0,
                                 p1,
                                 sourceId,
                                 out var correctionZeroStart))
                    {
                        if (p0OnWindowBoundary && p0.GetDistanceTo(correctionZeroStart) > maxWindowBoundaryInsetMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveStart = true;
                            movePlan.TargetStart = correctionZeroStart;
                        }
                    }
                    else if (IsEndpointOnHardBoundary(p0, sourceId))
                    {
                        alreadyOnHard++;
                    }
                    else if (TryFindExtensionTarget(
                                 p0,
                                 p1,
                                 sourceId,
                                 out var snappedStart,
                                 out var startAllowsUsec2012BoundaryMove))
                    {
                        var maxBoundaryMove = startAllowsUsec2012BoundaryMove
                            ? maxWindowBoundaryUsec2012Move
                            : maxWindowBoundaryInsetMove;
                        if (p0OnWindowBoundary && p0.GetDistanceTo(snappedStart) > maxBoundaryMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveStart = true;
                            movePlan.TargetStart = snappedStart;
                        }
                    }
                    else if (p0OnWindowBoundary)
                    {
                        boundarySkipped++;
                    }
                    else
                    {
                        noTarget++;
                    }

                    scannedEndpoints++;
                    var p1OnWindowBoundary = IsPointOnAnyWindowBoundary(p1, outerBoundaryTol);
                    if (TryFindRegularUsecCorridorTarget(
                            p1,
                            p0,
                            sourceId,
                            out var corridorEnd))
                    {
                        var maxBoundaryMove = maxWindowBoundaryUsec2012Move;
                        if (p1OnWindowBoundary && p1.GetDistanceTo(corridorEnd) > maxBoundaryMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveEnd = true;
                            movePlan.TargetEnd = corridorEnd;
                        }
                    }
                    else if (TryFindCorrectionOuterCompanionExtension(
                                 p1,
                                 p0,
                                 sourceId,
                                 out var correctionOuterCompanionEnd))
                    {
                        if (p1OnWindowBoundary && p1.GetDistanceTo(correctionOuterCompanionEnd) > maxWindowBoundaryInsetMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveEnd = true;
                            movePlan.TargetEnd = correctionOuterCompanionEnd;
                            endUsedCorrectionOuterCompanion = true;
                        }
                    }
                    else if (TryFindCorrectionZeroCompanionExtension(
                                 p1,
                                 p0,
                                 sourceId,
                                 out var correctionZeroEnd))
                    {
                        if (p1OnWindowBoundary && p1.GetDistanceTo(correctionZeroEnd) > maxWindowBoundaryInsetMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveEnd = true;
                            movePlan.TargetEnd = correctionZeroEnd;
                        }
                    }
                    else if (IsEndpointOnHardBoundary(p1, sourceId))
                    {
                        alreadyOnHard++;
                    }
                    else if (TryFindExtensionTarget(
                                 p1,
                                 p0,
                                 sourceId,
                                 out var snappedEnd,
                                 out var endAllowsUsec2012BoundaryMove))
                    {
                        var maxBoundaryMove = endAllowsUsec2012BoundaryMove
                            ? maxWindowBoundaryUsec2012Move
                            : maxWindowBoundaryInsetMove;
                        if (p1OnWindowBoundary && p1.GetDistanceTo(snappedEnd) > maxBoundaryMove)
                        {
                            boundarySkipped++;
                        }
                        else
                        {
                            movePlan.MoveEnd = true;
                            movePlan.TargetEnd = snappedEnd;
                        }
                    }
                    else if (p1OnWindowBoundary)
                    {
                        boundarySkipped++;
                    }
                    else
                    {
                        noTarget++;
                    }

                    CancelShorterEndpointMoveForMinimumLength(ref movePlan, p0, p1, minRemainingLength);

                    if (!movePlan.HasAnyMoves)
                    {
                        continue;
                    }

                    var moveResult = ApplyEndpointMovePlanForEndpointEnforcement(writable, movePlan, endpointMoveTol);
                    adjustedEndpoints += moveResult.AdjustedEndpointCount;
                    if (moveResult.MovedAny)
                    {
                        adjustedLines++;
                        if (moveSamples.Count < 20)
                        {
                            moveSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###} => {5:0.###},{6:0.###}->{7:0.###},{8:0.###}",
                                    sourceId.Handle.ToString(),
                                    p0.X,
                                    p0.Y,
                                    p1.X,
                                    p1.Y,
                                    movePlan.TargetStart.X,
                                    movePlan.TargetStart.Y,
                                    movePlan.TargetEnd.X,
                                    movePlan.TargetEnd.Y));
                        }
                    }

                    if (startUsedCorrectionOuterCompanion && movePlan.MoveStart)
                    {
                        TryEnsureLocalCorrectionZeroOverlapFromMovedVerticalEndpoint(movePlan.TargetStart, movePlan.TargetEnd, sourceId);
                    }

                    if (endUsedCorrectionOuterCompanion && movePlan.MoveEnd)
                    {
                        TryEnsureLocalCorrectionZeroOverlapFromMovedVerticalEndpoint(movePlan.TargetEnd, movePlan.TargetStart, sourceId);
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: L-SEC endpoint-on-hard rule scannedEndpoints={scannedEndpoints}, alreadyOnHard={alreadyOnHard}, windowBoundarySkipped={boundarySkipped}, noTarget={noTarget}, fallbackEndpointUsed={fallbackEndpointUsed}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
                for (var si = 0; si < moveSamples.Count; si++)
                {
                    logger?.WriteLine("Cleanup:   L-SEC moved " + moveSamples[si]);
                }
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

            bool IsZeroTwentySourceLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
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

                    var layer = ent.Layer ?? string.Empty;
                    if (IsCorrectionZeroBoundaryLayer(layer))
                    {
                        // Keep the full live correction-zero companion set available here.
                        // The 0/20 source lines remain scoped to the requested windows, but the
                        // matching correction-zero row can sit just beyond a section window edge.
                        correctionZeroSegments.Add(
                            (new LineDistancePoint(a.X, a.Y), new LineDistancePoint(b.X, b.Y)));
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
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
                const double maxWindowBoundaryInsetMove = CorrectionLineInsetMeters + 1.0;

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

                bool IsEndpointOnCorrectionZeroProjection(Point2d endpoint, Point2d otherEndpoint)
                {
                    const double projectionTouchTol = 0.25;
                    const double maxProjectionSegmentGap = CorrectionLineInsetMeters * 3.0;
                    if (endpoint.GetDistanceTo(otherEndpoint) <= 1e-6)
                    {
                        return false;
                    }

                    for (var i = 0; i < correctionZeroSegments.Count; i++)
                    {
                        var seg = correctionZeroSegments[i];
                        if (!TryIntersectInfiniteLinesForPluginGeometry(
                                endpoint,
                                otherEndpoint,
                                new Point2d(seg.A.X, seg.A.Y),
                                new Point2d(seg.B.X, seg.B.Y),
                                out var candidate))
                        {
                            continue;
                        }

                        if (candidate.GetDistanceTo(endpoint) <= projectionTouchTol &&
                            DistancePointToSegment(
                                candidate,
                                new Point2d(seg.A.X, seg.A.Y),
                                new Point2d(seg.B.X, seg.B.Y)) <= maxProjectionSegmentGap)
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

                    var sourceLayer = writable.Layer ?? string.Empty;
                    var movePlan = EndpointMovePlan.Create(p0, p1);

                    scannedEndpoints++;
                    var p0OnWindowBoundary = IsPointOnAnyWindowBoundary(p0, outerBoundaryTol);
                    if (p0OnWindowBoundary &&
                        TryProjectCorrectionZeroTarget(p0, p1, out var boundarySnappedStart) &&
                        p0.GetDistanceTo(boundarySnappedStart) <= maxWindowBoundaryInsetMove)
                    {
                        movePlan.MoveStart = true;
                        movePlan.TargetStart = boundarySnappedStart;
                    }
                    else if (p0OnWindowBoundary)
                    {
                        windowBoundarySkipped++;
                    }
                    else if (IsEndpointOnCorrectionZero(p0) || IsEndpointOnCorrectionZeroProjection(p0, p1))
                    {
                        alreadyOnCorrectionZero++;
                    }
                    else if (TryProjectCorrectionZeroTarget(p0, p1, out var snappedStart))
                    {
                        movePlan.MoveStart = true;
                        movePlan.TargetStart = snappedStart;
                    }
                    else
                    {
                        noTarget++;
                    }

                    scannedEndpoints++;
                    var p1OnWindowBoundary = IsPointOnAnyWindowBoundary(p1, outerBoundaryTol);
                    if (p1OnWindowBoundary &&
                        TryProjectCorrectionZeroTarget(p1, p0, out var boundarySnappedEnd) &&
                        p1.GetDistanceTo(boundarySnappedEnd) <= maxWindowBoundaryInsetMove)
                    {
                        movePlan.MoveEnd = true;
                        movePlan.TargetEnd = boundarySnappedEnd;
                    }
                    else if (p1OnWindowBoundary)
                    {
                        windowBoundarySkipped++;
                    }
                    else if (IsEndpointOnCorrectionZero(p1) || IsEndpointOnCorrectionZeroProjection(p1, p0))
                    {
                        alreadyOnCorrectionZero++;
                    }
                    else if (TryProjectCorrectionZeroTarget(p1, p0, out var snappedEnd))
                    {
                        movePlan.MoveEnd = true;
                        movePlan.TargetEnd = snappedEnd;
                    }
                    else
                    {
                        noTarget++;
                    }

                    CancelShorterEndpointMoveForMinimumLength(ref movePlan, p0, p1, minRemainingLength);

                    if (!movePlan.HasAnyMoves)
                    {
                        continue;
                    }

                    var moveResult = ApplyEndpointMovePlanForEndpointEnforcement(writable, movePlan, endpointMoveTol);
                    adjustedEndpoints += moveResult.AdjustedEndpointCount;
                    if (moveResult.MovedAny)
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

        private static void TrimZeroTwentyVerticalOverhangsToHardHorizontalSections(
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
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);
            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);
            bool IsPointOnAnyWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForEndpointEnforcement(p, tol, clipWindows);

            bool IsZeroTwentySourceLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsHardHorizontalSectionLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var hardHorizontalTargets = new List<(Point2d A, Point2d B, double MinX, double MaxX, double AxisY)>();
                var sourceIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsHardHorizontalSectionLayer(layer) && IsHorizontalLikeForEndpointEnforcement(a, b))
                    {
                        hardHorizontalTargets.Add((a, b, Math.Min(a.X, b.X), Math.Max(a.X, b.X), 0.5 * (a.Y + b.Y)));
                    }
                    else if (IsZeroTwentySourceLayer(layer) && IsVerticalLikeForEndpointEnforcement(a, b))
                    {
                        sourceIds.Add(id);
                    }
                }

                if (sourceIds.Count == 0 || hardHorizontalTargets.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTouchTol = 0.50;
                const double endpointMoveTol = 0.05;
                const double minMove = 0.05;
                const double maxTrim = 80.0;
                const double spanTol = 0.50;
                const double outerBoundaryTol = 0.40;

                var scannedEndpoints = 0;
                var alreadyOnHard = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;

                bool IsEndpointOnHardHorizontal(Point2d endpoint)
                {
                    for (var i = 0; i < hardHorizontalTargets.Count; i++)
                    {
                        var target = hardHorizontalTargets[i];
                        if (DistancePointToSegment(endpoint, target.A, target.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindBestHardHorizontalTrimTarget(
                    Point2d a,
                    Point2d b,
                    bool canTrimStart,
                    bool canTrimEnd,
                    out bool bestMoveStart,
                    out Point2d bestTarget)
                {
                    bestMoveStart = false;
                    bestTarget = default;
                    var bestFound = false;
                    var bestMoveDistance = double.MaxValue;
                    for (var ti = 0; ti < hardHorizontalTargets.Count; ti++)
                    {
                        var target = hardHorizontalTargets[ti];
                        if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var intersection))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(intersection, target.A, target.B) > spanTol)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(intersection, a, b) > spanTol)
                        {
                            continue;
                        }

                        var dStart = a.GetDistanceTo(intersection);
                        var dEnd = b.GetDistanceTo(intersection);
                        var moveStart = dStart <= dEnd;
                        if ((moveStart && !canTrimStart) || (!moveStart && !canTrimEnd))
                        {
                            continue;
                        }

                        var moveDistance = moveStart ? dStart : dEnd;
                        if (moveDistance <= minMove || moveDistance > maxTrim)
                        {
                            continue;
                        }

                        if (!bestFound || moveDistance < bestMoveDistance - 1e-6)
                        {
                            bestFound = true;
                            bestMoveStart = moveStart;
                            bestMoveDistance = moveDistance;
                            bestTarget = intersection;
                        }
                    }

                    return bestFound;
                }

                bool TryTrimVerticalSourceToHardHorizontal(ObjectId sourceId)
                {
                    if (!(tr.GetObject(sourceId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        return false;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var a, out var b) ||
                        !IsVerticalLikeForEndpointEnforcement(a, b))
                    {
                        return false;
                    }

                    var startOnHard = IsEndpointOnHardHorizontal(a);
                    var endOnHard = IsEndpointOnHardHorizontal(b);
                    if (startOnHard)
                    {
                        alreadyOnHard++;
                    }
                    if (endOnHard)
                    {
                        alreadyOnHard++;
                    }

                    var startOnBoundary = IsPointOnAnyWindowBoundary(a, outerBoundaryTol);
                    var endOnBoundary = IsPointOnAnyWindowBoundary(b, outerBoundaryTol);
                    var canTrimStart = !startOnHard && !startOnBoundary && endOnHard;
                    var canTrimEnd = !endOnHard && !endOnBoundary && startOnHard;
                    if (!canTrimStart && !canTrimEnd)
                    {
                        return false;
                    }

                    if (canTrimStart)
                    {
                        scannedEndpoints++;
                    }
                    if (canTrimEnd)
                    {
                        scannedEndpoints++;
                    }

                    if (!TryFindBestHardHorizontalTrimTarget(
                            a,
                            b,
                            canTrimStart,
                            canTrimEnd,
                            out var bestMoveStart,
                            out var bestTarget))
                    {
                        if (canTrimStart || canTrimEnd)
                        {
                            noTarget++;
                        }
                        return false;
                    }

                    if ((bestMoveStart && startOnBoundary) || (!bestMoveStart && endOnBoundary))
                    {
                        boundarySkipped++;
                        return false;
                    }

                    if (!TryMoveEndpoint(writable, bestMoveStart, bestTarget, endpointMoveTol))
                    {
                        return false;
                    }

                    adjustedEndpoints++;
                    adjustedLines++;
                    return true;
                }

                for (var i = 0; i < sourceIds.Count; i++)
                {
                    var sourceId = sourceIds[i];
                    TryTrimVerticalSourceToHardHorizontal(sourceId);
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: 0/20 vertical hard-horizontal trim scannedEndpoints={scannedEndpoints}, alreadyOnHard={alreadyOnHard}, boundarySkipped={boundarySkipped}, noTarget={noTarget}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
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
                       string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var boundarySegments = new List<(Point2d A, Point2d B)>();
                var boundaryEndpointClusters = new List<(Point2d Point, int IncidentCount)>();
                var correctionBoundarySegments = new List<(Point2d A, Point2d B)>();
                var correctionBoundarySegmentsWithLayers = new List<(Point2d A, Point2d B, string Layer)>();
                var qsecLineIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                const double boundaryJunctionClusterTol = 0.90;

                void AddBoundaryEndpointCluster(Point2d point)
                {
                    var bestIndex = -1;
                    var bestDistance = double.MaxValue;
                    for (var ci = 0; ci < boundaryEndpointClusters.Count; ci++)
                    {
                        var distance = boundaryEndpointClusters[ci].Point.GetDistanceTo(point);
                        if (distance > boundaryJunctionClusterTol || distance >= bestDistance)
                        {
                            continue;
                        }

                        bestIndex = ci;
                        bestDistance = distance;
                    }

                    if (bestIndex < 0)
                    {
                        boundaryEndpointClusters.Add((point, 1));
                        return;
                    }

                    var existing = boundaryEndpointClusters[bestIndex];
                    var nextCount = existing.IncidentCount + 1;
                    var nextPoint = new Point2d(
                        ((existing.Point.X * existing.IncidentCount) + point.X) / nextCount,
                        ((existing.Point.Y * existing.IncidentCount) + point.Y) / nextCount);
                    boundaryEndpointClusters[bestIndex] = (nextPoint, nextCount);
                }

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
                        AddBoundaryEndpointCluster(a);
                        AddBoundaryEndpointCluster(b);
                        if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        {
                            correctionBoundarySegments.Add((a, b));
                            correctionBoundarySegmentsWithLayers.Add((a, b, layer));
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
                var junctionSnapAdjustedEndpoints = 0;

                bool IsEndpointOnValidBoundary(Point2d endpoint) =>
                    IsEndpointOnBoundarySegmentsForEndpointEnforcement(endpoint, boundarySegments, endpointTouchTol);

                bool IsEndpointNearCorrectionBoundary(Point2d endpoint) =>
                    IsEndpointNearBoundarySegmentsForEndpointEnforcement(endpoint, correctionBoundarySegments, correctionAdjTol);

                bool TryGetQuarterEndpointOutwardDirection(
                    Point2d endpoint,
                    Point2d other,
                    out Vector2d outwardDir,
                    out double outwardLen)
                {
                    var outward = endpoint - other;
                    outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        outwardDir = default;
                        return false;
                    }

                    outwardDir = outward / outwardLen;
                    return true;
                }

                bool TryFindBoundaryJunctionSnapTarget(
                    Point2d endpoint,
                    Point2d other,
                    double maxPromotionDistance,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!TryGetQuarterEndpointOutwardDirection(endpoint, other, out var outwardDir, out var outwardLen))
                    {
                        return false;
                    }
                    const double junctionAxisTol = 1.25;
                    var found = false;
                    var bestAbsT = double.MaxValue;
                    var bestDegree = 0;
                    for (var ci = 0; ci < boundaryEndpointClusters.Count; ci++)
                    {
                        var cluster = boundaryEndpointClusters[ci];
                        if (cluster.IncidentCount < 2)
                        {
                            continue;
                        }

                        var candidate = cluster.Point;
                        if (endpoint.GetDistanceTo(candidate) <= endpointTouchTol)
                        {
                            return false;
                        }

                        if (DistancePointToInfiniteLine(candidate, endpoint, endpoint + outwardDir) > junctionAxisTol)
                        {
                            continue;
                        }

                        var t = (candidate - endpoint).DotProduct(outwardDir);
                        var absT = Math.Abs(t);
                        if (absT <= minMove || absT > maxPromotionDistance)
                        {
                            continue;
                        }

                        if (t < 0.0 && (outwardLen + t) < minRemainingLength)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            absT < (bestAbsT - 1e-6) ||
                            (Math.Abs(absT - bestAbsT) <= 1e-6 && cluster.IncidentCount > bestDegree);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestAbsT = absT;
                        bestDegree = cluster.IncidentCount;
                        target = candidate;
                    }

                    return found;
                }

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

                void ScanBoundarySegmentsForQuarterEndpointCandidates(
                    List<(Point2d A, Point2d B)> segments,
                    Point2d linePoint,
                    Vector2d lineDir,
                    double endpointAxisTolerance,
                    double apparentSegmentExtensionTol,
                    Action<double, bool> considerCandidate)
                {
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        if (TryIntersectInfiniteLineWithSegment(linePoint, lineDir, seg.A, seg.B, out var t))
                        {
                            considerCandidate(t, false);
                            continue;
                        }

                        if (TryIntersectInfiniteLineWithBoundedSegmentExtension(
                            linePoint,
                            lineDir,
                            seg.A,
                            seg.B,
                            apparentSegmentExtensionTol,
                            out var apparentT))
                        {
                            // Apparent intersection fallback for tiny boundary truncations.
                            considerCandidate(apparentT, true);
                        }
                    }

                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var candidate = endpointIndex == 0 ? seg.A : seg.B;
                            if (DistancePointToInfiniteLine(candidate, linePoint, linePoint + lineDir) > endpointAxisTolerance)
                            {
                                continue;
                            }

                            var t = (candidate - linePoint).DotProduct(lineDir);
                            considerCandidate(t, true);
                        }
                    }
                }

                bool TryFindSnapTarget(Point2d endpoint, Point2d other, out Point2d target)
            {
                target = endpoint;
                    if (!TryGetQuarterEndpointOutwardDirection(endpoint, other, out var outwardDir, out var outwardLen))
                    {
                        return false;
                    }
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

                    ScanBoundarySegmentsForQuarterEndpointCandidates(
                        boundarySegments,
                        endpoint,
                        outwardDir,
                        endpointAxisTol,
                        apparentSegmentExtensionTol,
                        ConsiderCandidate);

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
                    if (!TryGetQuarterEndpointOutwardDirection(endpoint, other, out var outwardDir, out var outwardLen))
                    {
                        return false;
                    }
                    var found = false;
                    var bestU = double.MaxValue;
                    var bestIsFallback = true;
                    var bestTerminalRank = int.MaxValue;
                    var bestLayerPriority = int.MaxValue;
                    var bestCompanionGap = double.MaxValue;
                    const double apparentSegmentExtensionTol = 6.0;

                    int GetCandidateTerminalRank(Point2d candidatePoint, string layerName)
                    {
                        if (string.IsNullOrWhiteSpace(layerName))
                        {
                            return 2;
                        }

                        var touchesOwnEndpoint = false;
                        var sameLayerIncidentCount = 0;
                        for (var ci = 0; ci < correctionBoundarySegmentsWithLayers.Count; ci++)
                        {
                            var seg = correctionBoundarySegmentsWithLayers[ci];
                            if (!string.Equals(seg.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var touchesA = seg.A.GetDistanceTo(candidatePoint) <= endpointAxisTol;
                            var touchesB = seg.B.GetDistanceTo(candidatePoint) <= endpointAxisTol;
                            if (!touchesA && !touchesB)
                            {
                                continue;
                            }

                            sameLayerIncidentCount++;
                            touchesOwnEndpoint = true;
                        }

                        if (!touchesOwnEndpoint)
                        {
                            return 2;
                        }

                        return sameLayerIncidentCount <= 1 ? 0 : 1;
                    }

                    void ConsiderCandidate(double u, bool isFallback, string? layerName)
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

                        var candidatePoint = other + (outwardDir * u);
                        var terminalRank = GetCandidateTerminalRank(candidatePoint, layerName ?? string.Empty);
                        var layerPriority = GetQuarterSouthCorrectionLayerPriority(layerName);
                        var companionGap = double.MaxValue;
                        if (layerPriority == GetQuarterSouthCorrectionLayerPriority(LayerUsecCorrection))
                        {
                            for (var ci = 0; ci < correctionBoundarySegmentsWithLayers.Count; ci++)
                            {
                                var companion = correctionBoundarySegmentsWithLayers[ci];
                                if (!string.Equals(companion.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var gap = DistancePointToSegment(candidatePoint, companion.A, companion.B);
                                if (gap < companionGap)
                                {
                                    companionGap = gap;
                                }
                            }

                            if (double.IsInfinity(companionGap) || double.IsNaN(companionGap) || companionGap == double.MaxValue)
                            {
                                companionGap = 0.0;
                            }
                        }
                        else
                        {
                            companionGap = 0.0;
                        }

                        var isBetter =
                            !found ||
                            terminalRank < bestTerminalRank ||
                            (terminalRank == bestTerminalRank &&
                                (layerPriority < bestLayerPriority ||
                                 (layerPriority == bestLayerPriority &&
                                     (companionGap < (bestCompanionGap - 1e-6) ||
                                      (Math.Abs(companionGap - bestCompanionGap) <= 1e-6 &&
                                           (u < (bestU - 1e-6) ||
                                            (Math.Abs(u - bestU) <= 1e-6 && bestIsFallback && !isFallback)))))));
                        if (!isBetter)
                        {
                            return;
                        }

                        found = true;
                        bestU = u;
                        bestIsFallback = isFallback;
                        bestTerminalRank = terminalRank;
                        bestLayerPriority = layerPriority;
                        bestCompanionGap = companionGap;
                    }

                    void ScanCorrectionBoundarySegmentsForQuarterEndpointCandidates(
                        Point2d linePoint,
                        Vector2d lineDir,
                        double endpointAxisTolerance,
                        double apparentSegmentExtensionTolerance)
                    {
                        for (var i = 0; i < correctionBoundarySegmentsWithLayers.Count; i++)
                        {
                            var seg = correctionBoundarySegmentsWithLayers[i];
                            if (TryIntersectInfiniteLineWithSegment(linePoint, lineDir, seg.A, seg.B, out var t))
                            {
                                ConsiderCandidate(t, false, seg.Layer);
                                continue;
                            }

                            if (TryIntersectInfiniteLineWithBoundedSegmentExtension(
                                    linePoint,
                                    lineDir,
                                    seg.A,
                                    seg.B,
                                    apparentSegmentExtensionTolerance,
                                    out var apparentT))
                            {
                                ConsiderCandidate(apparentT, true, seg.Layer);
                            }
                        }

                        for (var i = 0; i < correctionBoundarySegmentsWithLayers.Count; i++)
                        {
                            var seg = correctionBoundarySegmentsWithLayers[i];
                            var endpoints = new[] { seg.A, seg.B };
                            for (var endpointIndex = 0; endpointIndex < endpoints.Length; endpointIndex++)
                            {
                                var candidate = endpoints[endpointIndex];
                                if (DistancePointToInfiniteLine(candidate, linePoint, linePoint + lineDir) > endpointAxisTolerance)
                                {
                                    continue;
                                }

                                var t = (candidate - linePoint).DotProduct(lineDir);
                                ConsiderCandidate(t, true, seg.Layer);
                            }
                        }
                    }

                    // Correction-adjacent rule: scan correction boundaries first, but if the best
                    // correction candidate is effectively the current endpoint (no meaningful move),
                    // continue and allow generic hard boundaries to provide the actual projected hit.
                    ScanCorrectionBoundarySegmentsForQuarterEndpointCandidates(
                        other,
                        outwardDir,
                        endpointAxisTol,
                        apparentSegmentExtensionTol);
                    var correctionCandidateIsCurrent =
                        found && Math.Abs(bestU - outwardLen) <= endpointTouchTol;
                    if (!found || correctionCandidateIsCurrent)
                    {
                        ScanBoundarySegmentsForQuarterEndpointCandidates(
                            boundarySegments,
                            other,
                            outwardDir,
                            endpointAxisTol,
                            apparentSegmentExtensionTol,
                            (u, isFallback) => ConsiderCandidate(u, isFallback, null));
                    }

                    if (!found)
                    {
                        return false;
                    }

                    target = other + (outwardDir * bestU);
                return true;
            }

            bool TryPromoteQuarterEndpointMoveToBoundaryJunction(
                Point2d endpoint,
                Point2d other,
                double maxPromotionDistance,
                out Point2d target)
            {
                target = endpoint;
                if (!TryFindBoundaryJunctionSnapTarget(endpoint, other, maxPromotionDistance, out var junctionTarget))
                {
                    alreadyOnBoundary++;
                    return false;
                }

                target = junctionTarget;
                junctionSnapAdjustedEndpoints++;
                return true;
            }

            bool TryResolveQuarterEndpointMoveTarget(
                Point2d endpoint,
                Point2d other,
                Point2d snappedTarget,
                out Point2d target)
            {
                const double maxLocalBoundaryJunctionPromotion = 3.0;
                if (endpoint.GetDistanceTo(snappedTarget) <= endpointTouchTol)
                {
                    return TryPromoteQuarterEndpointMoveToBoundaryJunction(
                        endpoint,
                        other,
                        maxLocalBoundaryJunctionPromotion,
                        out target);
                }

                target = snappedTarget;
                return true;
            }

                bool TryResolveQuarterEndpointMove(
                Point2d endpoint,
                Point2d other,
                bool correctionAdjacent,
                out Point2d target)
            {
                const double maxLocalBoundaryJunctionPromotion = 3.0;
                target = endpoint;
                if (IsPointOnAnyWindowBoundary(endpoint, outerBoundaryTol))
                {
                    boundarySkipped++;
                    return false;
                }

                if (correctionAdjacent && TryFindCorrectionAdjacentSnapTarget(endpoint, other, out var correctionSnappedTarget))
                {
                    return TryResolveQuarterEndpointMoveTarget(endpoint, other, correctionSnappedTarget, out target);
                }

                if (!correctionAdjacent && IsEndpointOnValidBoundary(endpoint))
                {
                    return TryPromoteQuarterEndpointMoveToBoundaryJunction(
                        endpoint,
                        other,
                        maxLocalBoundaryJunctionPromotion,
                        out target);
                }

                if (TryFindSnapTarget(endpoint, other, out var genericSnappedTarget))
                {
                    return TryResolveQuarterEndpointMoveTarget(endpoint, other, genericSnappedTarget, out target);
                }

                if (IsEndpointOnValidBoundary(endpoint))
                {
                    alreadyOnBoundary++;
                    return false;
                }

                noTarget++;
                return false;
            }

            bool TryAdjustQuarterLineToBoundary(ObjectId id)
            {
                if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                {
                    return false;
                }

                if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1))
                {
                    return false;
                }

                var movePlan = EndpointMovePlan.Create(p0, p1);
                var p0CorrectionAdjacent = IsEndpointNearCorrectionBoundary(p0);
                var p1CorrectionAdjacent = IsEndpointNearCorrectionBoundary(p1);

                PlanQuarterEndpointMove(p0, p1, p0CorrectionAdjacent, moveStart: true, ref movePlan);
                PlanQuarterEndpointMove(p1, p0, p1CorrectionAdjacent, moveStart: false, ref movePlan);

                if (!movePlan.HasAnyMoves)
                {
                    return false;
                }

                var moveResult = ApplyEndpointMovePlanForEndpointEnforcement(writable, movePlan, endpointMoveTol);
                adjustedEndpoints += moveResult.AdjustedEndpointCount;
                if (moveResult.MovedAny)
                {
                    adjustedLines++;
                }

                return moveResult.MovedAny;
            }

            void PlanQuarterEndpointMove(
                Point2d endpoint,
                Point2d other,
                bool correctionAdjacent,
                bool moveStart,
                ref EndpointMovePlan movePlan)
            {
                scannedEndpoints++;
                if (!TryResolveQuarterEndpointMove(endpoint, other, correctionAdjacent, out var resolvedTarget))
                {
                    return;
                }

                if (moveStart)
                {
                    movePlan.MoveStart = true;
                    movePlan.TargetStart = resolvedTarget;
                }
                else
                {
                    movePlan.MoveEnd = true;
                    movePlan.TargetEnd = resolvedTarget;
                }
            }

            for (var i = 0; i < qsecLineIds.Count; i++)
            {
                var id = qsecLineIds[i];
                TryAdjustQuarterLineToBoundary(id);
            }

            tr.Commit();
            logger?.WriteLine(
                $"Cleanup: 1/4 endpoint-on-section rule scanned={scannedEndpoints}, alreadyOnBoundary={alreadyOnBoundary}, junctionSnapAdjustedEndpoints={junctionSnapAdjustedEndpoints}, windowBoundarySkipped={boundarySkipped}, noTarget={noTarget}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
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
                ["CORR"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["CORRZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
            };
            var verticalByKind = new Dictionary<string, List<(Point2d A, Point2d B, Point2d Mid)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["SEC"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["ZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["TWENTY"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["BLIND"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["CORR"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
                ["CORRZERO"] = new List<(Point2d A, Point2d B, Point2d Mid)>(),
            };
            var correctionBoundarySegmentsWithLayers = new List<(Point2d A, Point2d B, string Layer)>();
            var correctionZeroHorizontal = new List<(Point2d A, Point2d B, Point2d Mid)>();
            var qsecSegments = new List<(Point2d A, Point2d B)>();
            var lsdLineIds = new List<ObjectId>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var quarterContexts = new List<LsdQuarterContext>();

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

                    quarterContexts.Add(new LsdQuarterContext(
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

                    var hasPrimarySegment = TryReadOpenSegmentForEndpointEnforcement(ent, out var primaryA, out var primaryB);
                    var layer = ent.Layer ?? string.Empty;
                    var isCorrectionLayer =
                        IsCorrectionZeroLayer(layer) ||
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
                    var scopedSegments = CollectScopedEntitySegmentsForEndpointEnforcement(
                        ent,
                        hasPrimarySegment,
                        primaryA,
                        primaryB,
                        DoesSegmentIntersectAnyWindow,
                        fallbackToAllSegments: isCorrectionLayer);
                    if (scopedSegments.Count == 0)
                    {
                        continue;
                    }

                    if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        for (var si = 0; si < scopedSegments.Count; si++)
                        {
                            var seg = scopedSegments[si];
                            qsecSegments.Add((seg.A, seg.B));
                        }

                        continue;
                    }

                    if (string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        var hasAdjustableSegment = false;
                        for (var si = 0; si < scopedSegments.Count; si++)
                        {
                            var seg = scopedSegments[si];
                            if (!IsAdjustableLsdLineSegment(seg.A, seg.B))
                            {
                                continue;
                            }

                            hasAdjustableSegment = true;
                            break;
                        }

                        if (hasAdjustableSegment)
                        {
                            lsdLineIds.Add(id);
                        }

                        continue;
                    }

                    for (var si = 0; si < scopedSegments.Count; si++)
                    {
                        var a = scopedSegments[si].A;
                        var b = scopedSegments[si].B;
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
                            AddBoundarySegmentByKind("CORR", a, b);
                            correctionBoundarySegmentsWithLayers.Add((a, b, layer));
                        }
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
                        if (!IsPointWithinQuarterSectionBounds(
                                mid,
                                eastUnit,
                                northUnit,
                                sectionMinU,
                                sectionMaxU,
                                sectionMinV,
                                sectionMaxV,
                                sectionScopePad))
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

                    quarterContexts[qi] = new LsdQuarterContext(
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

                bool IsPointWithinQuarterSectionBounds(
                    Point2d point,
                    Vector2d eastUnit,
                    Vector2d northUnit,
                    double sectionMinU,
                    double sectionMaxU,
                    double sectionMinV,
                    double sectionMaxV,
                    double sectionScopePad)
                {
                    var targetU = ProjectOnAxis(point, eastUnit);
                    var targetV = ProjectOnAxis(point, northUnit);
                    return
                        targetU >= (sectionMinU - sectionScopePad) &&
                        targetU <= (sectionMaxU + sectionScopePad) &&
                        targetV >= (sectionMinV - sectionScopePad) &&
                        targetV <= (sectionMaxV + sectionScopePad);
                }

                bool IsPointWithinQuarterSectionScope(Point2d point, LsdQuarterContext context, double sectionScopePad)
                {
                    return IsPointWithinQuarterSectionBounds(
                        point,
                        context.EastUnit,
                        context.NorthUnit,
                        context.SectionMinU,
                        context.SectionMaxU,
                        context.SectionMinV,
                        context.SectionMaxV,
                        sectionScopePad);
                }

                void GetQuarterBoundaryExteriorSideExpectation(
                    LsdQuarterContext context,
                    out double boundaryV,
                    out double expectedExteriorSign)
                {
                    var boundaryPoint = IsSouthQuarter(context.Quarter)
                        ? context.BottomAnchor
                        : context.TopAnchor;
                    boundaryV = ProjectOnAxis(boundaryPoint, context.NorthUnit);
                    expectedExteriorSign = IsSouthQuarter(context.Quarter) ? -1.0 : 1.0;
                }

                const string boundaryKindSec = "SEC";
                const string boundaryKindZero = "ZERO";
                const string boundaryKindTwenty = "TWENTY";
                const string boundaryKindBlind = "BLIND";
                const string boundaryKindCorrection = "CORR";
                const string boundaryKindCorrectionZero = "CORRZERO";
                string[] secBoundaryKinds = { boundaryKindSec };

                List<string> BuildFallbackOuterKinds(bool lineIsHorizontal, LsdQuarterContext context)
                {
                    var fallbackKinds = new List<string>();

                    void AddKindPair(string primaryKind)
                    {
                        fallbackKinds.Add(primaryKind);
                        fallbackKinds.Add(boundaryKindSec);
                    }

                    if (lineIsHorizontal)
                    {
                        if (IsWestQuarter(context.Quarter))
                        {
                            AddKindPair(boundaryKindTwenty);
                        }
                        else if (IsEastQuarter(context.Quarter))
                        {
                            AddKindPair(boundaryKindZero);
                        }

                        return fallbackKinds;
                    }

                    if (IsSouthQuarter(context.Quarter))
                    {
                        AddKindPair(IsGroupASection(context.SectionNumber) ? boundaryKindTwenty : boundaryKindBlind);
                    }
                    else if (IsNorthQuarter(context.Quarter))
                    {
                        AddKindPair(IsGroupASection(context.SectionNumber) ? boundaryKindBlind : boundaryKindZero);
                    }

                    return fallbackKinds;
                }

                List<string> BuildPreferredOuterKinds(
                    Point2d outerPoint,
                    bool lineIsHorizontal,
                    LsdQuarterContext context,
                    out bool correctionOverride)
                {
                    correctionOverride = !lineIsHorizontal &&
                        IsPointNearAnySegment(outerPoint, correctionZeroHorizontal, 60.0);
                    if (correctionOverride)
                    {
                        return new List<string> { boundaryKindCorrection, boundaryKindCorrectionZero, boundaryKindSec };
                    }

                    return BuildFallbackOuterKinds(lineIsHorizontal, context);
                }

                bool TryFindBoundaryStationTargetInContext(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    bool lineIsHorizontal,
                    LsdQuarterContext context,
                    IReadOnlyList<string> preferredKinds,
                    out Point2d target)
                {
                    return TryFindBoundaryStationTarget(
                        endpoint,
                        innerEndpoint,
                        lineIsHorizontal,
                        context.EastUnit,
                        context.NorthUnit,
                        context.SectionMinU,
                        context.SectionMaxU,
                        context.SectionMinV,
                        context.SectionMaxV,
                        preferredKinds,
                        out target);
                }

                bool HasProjectedRoadAllowanceCandidateAtStation(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    bool lineIsHorizontal,
                    LsdQuarterContext context,
                    string kind)
                {
                    return HasProjectedRoadAllowanceCandidateAtEndpointStation(
                        endpoint,
                        innerEndpoint,
                        lineIsHorizontal,
                        context.EastUnit,
                        context.NorthUnit,
                        context.SectionMinU,
                        context.SectionMaxU,
                        context.SectionMinV,
                        context.SectionMaxV,
                        kind);
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

                bool TryResolveQuarterCorrectionSouthBoundary(
                    LsdQuarterContext context,
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
                    LsdQuarterContext context)
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

                bool TryResolveSouthQuarterLiveQsecPoint(
                    LsdQuarterContext context,
                    out Point2d southQsecPoint)
                {
                    southQsecPoint = default;
                    if (!IsSouthQuarter(context.Quarter) || qsecSegments.Count == 0)
                    {
                        return false;
                    }

                    const double sectionScopePad = 8.0;
                    var candidates = new List<(Point2d SouthPoint, double AxisGap, double SouthV, double SpanGap)>();
                    for (var qsecIndex = 0; qsecIndex < qsecSegments.Count; qsecIndex++)
                    {
                        var seg = qsecSegments[qsecIndex];
                        var mid = Midpoint(seg.A, seg.B);
                        if (!IsPointWithinQuarterSectionScope(mid, context, sectionScopePad))
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

                        var aU = ProjectOnAxis(seg.A, context.EastUnit);
                        var bU = ProjectOnAxis(seg.B, context.EastUnit);
                        var axisU = 0.5 * (aU + bU);
                        var axisGap = Math.Abs(axisU - context.SectionMidU);
                        var aV = ProjectOnAxis(seg.A, context.NorthUnit);
                        var bV = ProjectOnAxis(seg.B, context.NorthUnit);
                        var southV = Math.Min(aV, bV);
                        var spanGap = DistanceToClosedInterval(
                            context.SectionMidV,
                            southV,
                            Math.Max(aV, bV));
                        candidates.Add((aV <= bV ? seg.A : seg.B, axisGap, southV, spanGap));
                    }

                    if (candidates.Count == 0)
                    {
                        return false;
                    }

                    var minAxisGap = double.MaxValue;
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        if (candidates[i].AxisGap < minAxisGap)
                        {
                            minAxisGap = candidates[i].AxisGap;
                        }
                    }

                    const double southHalfAxisWindow = 6.0;
                    var foundSouthQsec = false;
                    var bestQsecAxisGap = double.MaxValue;
                    var bestSouthV = double.MaxValue;
                    var bestQsecSpanGap = double.MaxValue;
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var candidate = candidates[i];
                        if (candidate.AxisGap > minAxisGap + southHalfAxisWindow)
                        {
                            continue;
                        }

                        var better =
                            !foundSouthQsec ||
                            candidate.SouthV < (bestSouthV - 1e-6) ||
                            (Math.Abs(candidate.SouthV - bestSouthV) <= 1e-6 &&
                                (candidate.AxisGap < (bestQsecAxisGap - 1e-6) ||
                                 (Math.Abs(candidate.AxisGap - bestQsecAxisGap) <= 1e-6 && candidate.SpanGap < bestQsecSpanGap - 1e-6)));
                        if (!better)
                        {
                            continue;
                        }

                        foundSouthQsec = true;
                        bestSouthV = candidate.SouthV;
                        bestQsecAxisGap = candidate.AxisGap;
                        bestQsecSpanGap = candidate.SpanGap;
                        southQsecPoint = candidate.SouthPoint;
                    }

                    return foundSouthQsec;
                }

                bool TryFindQuarterResolvedCorrectionSouthBoundaryTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!IsSouthQuarter(context.Quarter))
                    {
                        return false;
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

                bool TryFindQuarterResolvedCorrectionSouthHalfMidTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!IsSouthQuarter(context.Quarter))
                    {
                        return false;
                    }

                    if (!horizontalByKind.TryGetValue("CORR", out var correctionSegments) ||
                        correctionSegments == null ||
                        correctionSegments.Count == 0)
                    {
                        return false;
                    }

                    if (!TryResolveSouthQuarterLiveQsecPoint(context, out var southQsecPoint))
                    {
                        return false;
                    }

                    const double connectTol = 1.0;
                    const double sectionScopePadWide = 40.0;
                    const double minMove = 0.005;
                    const double maxMove = 100.0;
                    var outward = endpoint - innerEndpoint;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    var outwardDir = outward / outwardLen;
                    var preferWestSide = context.Quarter == QuarterSelection.SouthWest;
                    var found = false;
                    var bestTarget = endpoint;
                    var bestConnectionGap = double.MaxValue;
                    var bestSideDistance = double.MinValue;
                    for (var segmentIndex = 0; segmentIndex < correctionSegments.Count; segmentIndex++)
                    {
                        var seg = correctionSegments[segmentIndex];
                        var segDistance = DistancePointToSegment(southQsecPoint, seg.A, seg.B);
                        var aTouches = seg.A.GetDistanceTo(southQsecPoint) <= connectTol;
                        var bTouches = seg.B.GetDistanceTo(southQsecPoint) <= connectTol;
                        if (!aTouches && !bTouches && segDistance > connectTol)
                        {
                            continue;
                        }

                        var connectionPoint = southQsecPoint;
                        if (aTouches)
                        {
                            connectionPoint = seg.A;
                        }
                        else if (bTouches)
                        {
                            connectionPoint = seg.B;
                        }
                        else
                        {
                            connectionPoint = ClosestPointOnSegmentForEndpointEnforcement(southQsecPoint, seg.A, seg.B);
                        }

                        var endpointAu = ProjectOnAxis(seg.A, context.EastUnit);
                        var endpointBu = ProjectOnAxis(seg.B, context.EastUnit);
                        var connectionU = ProjectOnAxis(connectionPoint, context.EastUnit);
                        var outerEndpoint = preferWestSide
                            ? (endpointAu <= endpointBu ? seg.A : seg.B)
                            : (endpointAu >= endpointBu ? seg.A : seg.B);
                        var sideDistance = Math.Abs(ProjectOnAxis(outerEndpoint, context.EastUnit) - connectionU);
                        if (sideDistance <= 2.0)
                        {
                            continue;
                        }

                        var targetPoint = Midpoint(connectionPoint, outerEndpoint);
                        if (!IsPointWithinQuarterSectionScope(targetPoint, context, sectionScopePadWide))
                        {
                            continue;
                        }

                        if ((targetPoint - innerEndpoint).DotProduct(outwardDir) < 2.0)
                        {
                            continue;
                        }

                        var move = endpoint.GetDistanceTo(targetPoint);
                        if (move <= minMove || move > maxMove)
                        {
                            continue;
                        }

                        var connectionGap = southQsecPoint.GetDistanceTo(connectionPoint);
                        var better =
                            !found ||
                            connectionGap < (bestConnectionGap - 1e-6) ||
                            (Math.Abs(connectionGap - bestConnectionGap) <= 1e-6 && sideDistance > bestSideDistance + 1e-6);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestTarget = targetPoint;
                        bestConnectionGap = connectionGap;
                        bestSideDistance = sideDistance;
                    }

                    if (!found)
                    {
                        return false;
                    }

                    const double targetScopePad = 40.0;
                    if (!IsPointWithinQuarterSectionScope(bestTarget, context, targetScopePad))
                    {
                        return false;
                    }

                    if ((bestTarget - innerEndpoint).DotProduct(outwardDir) < 2.0)
                    {
                        return false;
                    }

                    target = bestTarget;
                    return true;
                }

                bool TryFindPreferredQuarterCorrectionSouthTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
                    out Point2d target,
                    out bool usedHardBoundary,
                    out bool usedResolvedZero,
                    out bool usedInteriorZero)
                {
                    target = endpoint;
                    usedHardBoundary = false;
                    usedResolvedZero = false;
                    usedInteriorZero = false;

                    var foundHard = TryFindQuarterResolvedCorrectionSouthBoundaryTarget(
                        endpoint,
                        innerEndpoint,
                        context,
                        out var hardTarget);

                    var foundZero = TryFindQuarterResolvedCorrectionZeroTarget(
                        endpoint,
                        innerEndpoint,
                        context,
                        out var zeroTarget);
                    if (foundZero)
                    {
                        if (TrySnapCorrectionZeroTargetToLiveSegment(
                                zeroTarget,
                                endpoint,
                                innerEndpoint,
                                context,
                                out var snappedZeroTarget))
                        {
                            zeroTarget = snappedZeroTarget;
                        }
                    }
                    else
                    {
                        foundZero = TryFindQuarterInteriorCorrectionZeroTarget(
                            endpoint,
                            innerEndpoint,
                            context,
                            out zeroTarget);
                        if (foundZero &&
                            TrySnapCorrectionZeroTargetToLiveSegment(
                                zeroTarget,
                                endpoint,
                                innerEndpoint,
                                context,
                                out var snappedInteriorZeroTarget))
                        {
                            zeroTarget = snappedInteriorZeroTarget;
                        }
                    }

                    if (foundHard && foundZero)
                    {
                        var hardMove = endpoint.GetDistanceTo(hardTarget);
                        var zeroMove = endpoint.GetDistanceTo(zeroTarget);
                        var hardInsetScore = Math.Abs(hardMove - CorrectionLineInsetMeters);
                        var zeroInsetScore = Math.Abs(zeroMove - CorrectionLineInsetMeters);
                        var useHard =
                            hardInsetScore < zeroInsetScore - 1e-6 ||
                            (Math.Abs(hardInsetScore - zeroInsetScore) <= 1e-6 &&
                             hardMove < zeroMove - 1e-6);
                        if (useHard)
                        {
                            target = hardTarget;
                            usedHardBoundary = true;
                            return true;
                        }

                        target = zeroTarget;
                        usedResolvedZero = true;
                        return true;
                    }

                    if (foundHard)
                    {
                        target = hardTarget;
                        usedHardBoundary = true;
                        return true;
                    }

                    if (foundZero)
                    {
                        target = zeroTarget;
                        usedResolvedZero = true;
                        return true;
                    }

                    return false;
                }

                bool TryFindQuarterCorrectionBandShiftTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!IsSouthQuarter(context.Quarter) || correctionBoundarySegmentsWithLayers.Count == 0)
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
                    GetQuarterBoundaryExteriorSideExpectation(context, out var boundaryV, out var expectedExteriorSign);
                    const double sectionScopePad = 40.0;
                    const double minMove = 0.50;
                    const double maxMove = CorrectionLineInsetMeters * 2.5;
                    const double minOutwardAdvance = 2.0;
                    const double boundarySideTol = 0.75;
                    var found = false;
                    var bestTarget = endpoint;
                    var bestInsetScore = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < correctionBoundarySegmentsWithLayers.Count; i++)
                    {
                        var seg = correctionBoundarySegmentsWithLayers[i];
                        if (Math.Abs(seg.B.X - seg.A.X) < Math.Abs(seg.B.Y - seg.A.Y))
                        {
                            continue;
                        }

                        var segmentDelta = seg.B - seg.A;
                        var segmentLen2 = segmentDelta.DotProduct(segmentDelta);
                        if (segmentLen2 <= 1e-9)
                        {
                            continue;
                        }

                        var t = ((endpoint - seg.A).DotProduct(segmentDelta)) / segmentLen2;
                        if (t < 0.0)
                        {
                            t = 0.0;
                        }
                        else if (t > 1.0)
                        {
                            t = 1.0;
                        }

                        var targetPoint = seg.A + (segmentDelta * t);
                        if (!IsPointWithinQuarterSectionScope(targetPoint, context, sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (targetPoint - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var targetV = ProjectOnAxis(targetPoint, context.NorthUnit);
                        var signedBoundaryGap = targetV - boundaryV;
                        if ((expectedExteriorSign > 0.0 && signedBoundaryGap < -boundarySideTol) ||
                            (expectedExteriorSign < 0.0 && signedBoundaryGap > boundarySideTol))
                        {
                            continue;
                        }

                        var move = endpoint.GetDistanceTo(targetPoint);
                        if (move < minMove || move > maxMove)
                        {
                            continue;
                        }

                        var insetScore = Math.Abs(move - CorrectionLineInsetMeters);
                        if (!found ||
                            insetScore < bestInsetScore - 1e-6 ||
                            (Math.Abs(insetScore - bestInsetScore) <= 1e-6 &&
                             move < bestMove - 1e-6))
                        {
                            found = true;
                            bestTarget = targetPoint;
                            bestInsetScore = insetScore;
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

                bool TryFindQuarterCorrectionZeroMidpointTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!IsSouthQuarter(context.Quarter) ||
                        correctionZeroHorizontal.Count == 0)
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
                    var endpointStation = ProjectOnAxis(endpoint, context.EastUnit);
                    GetQuarterBoundaryExteriorSideExpectation(context, out var boundaryV, out var expectedExteriorSign);
                    const double sectionScopePad = 40.0;
                    const double stationTol = 12.0;
                    const double minOutwardAdvance = 2.0;
                    const double minMove = 0.005;
                    const double maxMove = 140.0;
                    const double boundarySideTol = 0.75;
                    var foundInset = false;
                    var bestInsetTarget = endpoint;
                    var bestInsetTargetError = double.MaxValue;
                    var bestInsetMove = double.MaxValue;
                    var bestInsetAxisOffset = double.MaxValue;
                    var foundFallback = false;
                    var bestFallbackTarget = endpoint;
                    var bestFallbackMove = double.MaxValue;
                    var bestFallbackBoundaryGap = double.MaxValue;
                    var bestFallbackAxisOffset = double.MaxValue;

                    for (var i = 0; i < correctionZeroHorizontal.Count; i++)
                    {
                        var seg = correctionZeroHorizontal[i];
                        var aStation = ProjectOnAxis(seg.A, context.EastUnit);
                        var bStation = ProjectOnAxis(seg.B, context.EastUnit);
                        var minStation = Math.Min(aStation, bStation) - stationTol;
                        var maxStation = Math.Max(aStation, bStation) + stationTol;
                        if (endpointStation < minStation || endpointStation > maxStation)
                        {
                            continue;
                        }

                        var midpoint = seg.Mid;
                        if (!IsPointWithinQuarterSectionScope(midpoint, context, sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (midpoint - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var targetV = ProjectOnAxis(midpoint, context.NorthUnit);
                        var signedBoundaryGap = targetV - boundaryV;
                        if ((expectedExteriorSign > 0.0 && signedBoundaryGap < -boundarySideTol) ||
                            (expectedExteriorSign < 0.0 && signedBoundaryGap > boundarySideTol))
                        {
                            continue;
                        }

                        var move = endpoint.GetDistanceTo(midpoint);
                        if (move <= minMove || move > maxMove)
                        {
                            continue;
                        }

                        var boundaryGap = Math.Abs(signedBoundaryGap);
                        var axisOffset = Math.Abs(midpoint.X - endpoint.X);
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
                                targetError < bestInsetTargetError - 1e-6 ||
                                (Math.Abs(targetError - bestInsetTargetError) <= 1e-6 &&
                                 move < bestInsetMove - 1e-6) ||
                                (Math.Abs(targetError - bestInsetTargetError) <= 1e-6 &&
                                 Math.Abs(move - bestInsetMove) <= 1e-6 &&
                                 axisOffset < bestInsetAxisOffset - 1e-6))
                            {
                                foundInset = true;
                                bestInsetTarget = midpoint;
                                bestInsetTargetError = targetError;
                                bestInsetMove = move;
                                bestInsetAxisOffset = axisOffset;
                            }

                            continue;
                        }

                        if (foundFallback &&
                            move >= bestFallbackMove - 1e-6 &&
                            boundaryGap >= bestFallbackBoundaryGap - 1e-6 &&
                            axisOffset >= bestFallbackAxisOffset - 1e-6)
                        {
                            continue;
                        }

                        foundFallback = true;
                        bestFallbackTarget = midpoint;
                        bestFallbackMove = move;
                        bestFallbackBoundaryGap = boundaryGap;
                        bestFallbackAxisOffset = axisOffset;
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

                bool TryFindQuarterInteriorCorrectionZeroTarget(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
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
                    GetQuarterBoundaryExteriorSideExpectation(context, out var boundaryV, out var expectedExteriorSign);
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

                        if (!IsPointWithinQuarterSectionScope(targetPoint, context, sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (targetPoint - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var targetV = ProjectOnAxis(targetPoint, context.NorthUnit);
                        var signedBoundaryGap = targetV - boundaryV;
                        if ((expectedExteriorSign > 0.0 && signedBoundaryGap < -boundarySideTol) ||
                            (expectedExteriorSign < 0.0 && signedBoundaryGap > boundarySideTol))
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
                    LsdQuarterContext context,
                    out Point2d target)
                {
                    target = endpoint;
                    if (!IsSouthQuarter(context.Quarter) || correctionBoundarySegmentsWithLayers.Count == 0)
                    {
                        return false;
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
                    LsdQuarterContext context,
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
                    GetQuarterBoundaryExteriorSideExpectation(context, out var boundaryV, out var expectedExteriorSign);
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

                        if (!IsPointWithinQuarterSectionScope(candidate, context, sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (candidate - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var targetV = ProjectOnAxis(candidate, context.NorthUnit);
                        var signedBoundaryGap = targetV - boundaryV;
                        if ((expectedExteriorSign > 0.0 && signedBoundaryGap < -boundarySideTol) ||
                            (expectedExteriorSign < 0.0 && signedBoundaryGap > boundarySideTol))
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

                bool TrySnapCorrectionZeroTargetToInsetMidpoint(
                    Point2d resolvedTarget,
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    LsdQuarterContext context,
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
                    const double sectionScopePad = 40.0;
                    const double onRowTol = 0.35;
                    const double maxMidpointDelta = 8.0;
                    const double minOutwardAdvance = 2.0;

                    var found = false;
                    var bestTarget = resolvedTarget;
                    var bestTargetDelta = double.MaxValue;
                    for (var i = 0; i < correctionZeroHorizontal.Count; i++)
                    {
                        var seg = correctionZeroHorizontal[i];
                        if (DistancePointToSegment(resolvedTarget, seg.A, seg.B) > onRowTol)
                        {
                            continue;
                        }

                        var midpoint = seg.Mid;
                        if (!IsPointWithinQuarterSectionScope(midpoint, context, sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (midpoint - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance < minOutwardAdvance)
                        {
                            continue;
                        }

                        var targetDelta = resolvedTarget.GetDistanceTo(midpoint);
                        if (targetDelta > maxMidpointDelta)
                        {
                            continue;
                        }

                        if (!found ||
                            targetDelta < bestTargetDelta - 1e-6)
                        {
                            found = true;
                            bestTarget = midpoint;
                            bestTargetDelta = targetDelta;
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
                    out LsdQuarterContext context)
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

                bool IsPointNearAnyHardBoundary(Point2d p, double tol = 0.40)
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
                            if (DistancePointToSegment(p, seg.A, seg.B) <= tol)
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
                        (horizontalByKind.TryGetValue("CORR", out var hCorr) && NearAny(hCorr)) ||
                        (horizontalByKind.TryGetValue("CORRZERO", out var hCorrZero) && NearAny(hCorrZero)) ||
                        (verticalByKind.TryGetValue("SEC", out var vSec) && NearAny(vSec)) ||
                        (verticalByKind.TryGetValue("ZERO", out var vZero) && NearAny(vZero)) ||
                        (verticalByKind.TryGetValue("TWENTY", out var vTwenty) && NearAny(vTwenty)) ||
                        (verticalByKind.TryGetValue("CORR", out var vCorr) && NearAny(vCorr)) ||
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
                            var stationReference =
                                !lineIsHorizontal &&
                                (string.Equals(kind, "CORRZERO", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(kind, "CORR", StringComparison.OrdinalIgnoreCase))
                                    ? innerEndpoint
                                    : endpoint;
                            if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                    stationReference,
                                    seg.A,
                                    seg.B,
                                    stationAxis,
                                    stationTol,
                                    out var preservePoint))
                            {
                                continue;
                            }

                            if (!IsPointWithinQuarterSectionBounds(
                                    preservePoint,
                                    eastUnit,
                                    northUnit,
                                    sectionMinU,
                                    sectionMaxU,
                                    sectionMinV,
                                    sectionMaxV,
                                    sectionScopePad))
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
                            var stationReference =
                                !lineIsHorizontal &&
                                (string.Equals(kind, "CORRZERO", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(kind, "CORR", StringComparison.OrdinalIgnoreCase))
                                    ? innerEndpoint
                                    : endpoint;
                            if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                    stationReference,
                                    seg.A,
                                    seg.B,
                                    stationAxis,
                                    stationTol,
                                    out var targetPoint))
                            {
                                continue;
                            }

                            if (!IsPointWithinQuarterSectionBounds(
                                    targetPoint,
                                    eastUnit,
                                    northUnit,
                                    sectionMinU,
                                    sectionMaxU,
                                    sectionMinV,
                                    sectionMaxV,
                                    sectionScopePad))
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

                            var correctionCompanionPenalty = 0.0;
                            if (string.Equals(kind, "CORR", StringComparison.OrdinalIgnoreCase) &&
                                source.TryGetValue("CORRZERO", out var correctionZeroSegments) &&
                                correctionZeroSegments.Count > 0)
                            {
                                var bestCompanionGap = double.MaxValue;
                                for (var czi = 0; czi < correctionZeroSegments.Count; czi++)
                                {
                                    var correctionZeroSeg = correctionZeroSegments[czi];
                                    var gap = DistancePointToSegment(targetPoint, correctionZeroSeg.A, correctionZeroSeg.B);
                                    if (gap < bestCompanionGap)
                                    {
                                        bestCompanionGap = gap;
                                    }
                                }

                                if (bestCompanionGap < double.MaxValue)
                                {
                                    correctionCompanionPenalty = bestCompanionGap * 2000.0;
                                }
                            }

                            var score = (pi * 1000000.0) + correctionCompanionPenalty + hardLinkPenalty + (outwardAdvance * 1000.0) + (axisGap * 100.0) + move;
                            if (score >= bestScore)
                            {
                                continue;
                            }

                            bestScore = score;
                            target = targetPoint;
                            found = true;
                        }
                    }

                    if (found &&
                        preserveOnPrimaryBoundary &&
                        preferredKinds.Count == 1 &&
                        string.Equals(preferredKinds[0], "SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        // Survey SEC-target fallback should preserve an endpoint that already lands on
                        // the authoritative surveyed section boundary instead of walking to a farther
                        // parallel SEC row.
                        target = endpoint;
                        return true;
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

                bool HasProjectedRoadAllowanceCandidateAtEndpointStation(
                    Point2d endpoint,
                    Point2d innerEndpoint,
                    bool lineIsHorizontal,
                    Vector2d eastUnit,
                    Vector2d northUnit,
                    double sectionMinU,
                    double sectionMaxU,
                    double sectionMinV,
                    double sectionMaxV,
                    string kind)
                {
                    var source = lineIsHorizontal ? verticalByKind : horizontalByKind;
                    if (!source.TryGetValue(kind, out var segments) || segments.Count == 0)
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
                    var stationAxis = lineIsHorizontal ? northUnit : eastUnit;
                    const double stationTol = 8.0;
                    const double sectionScopePad = 40.0;
                    const double minOutwardAdvance = 2.0;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        if (!TryResolveSegmentPointAtProjectedStationForEndpointEnforcement(
                                endpoint,
                                seg.A,
                                seg.B,
                                stationAxis,
                                stationTol,
                                out var projectedPoint))
                        {
                            continue;
                        }

                        if (!IsPointWithinQuarterSectionBounds(
                                projectedPoint,
                                eastUnit,
                                northUnit,
                                sectionMinU,
                                sectionMaxU,
                                sectionMinV,
                                sectionMaxV,
                                sectionScopePad))
                        {
                            continue;
                        }

                        var outwardAdvance = (projectedPoint - innerEndpoint).DotProduct(outwardDir);
                        if (outwardAdvance >= minOutwardAdvance)
                        {
                            return true;
                        }
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
                                if (!IsPointWithinQuarterSectionScope(mid, context, sectionScopePad))
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
                            if (!IsPointWithinQuarterSectionScope(mid, context, sectionScopePad))
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
                    var preferredKinds = BuildPreferredOuterKinds(
                        outerPoint,
                        lineIsHorizontal,
                        context,
                        out var correctionOverride);
                    if (correctionOverride)
                    {
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

                    bool TryFindSecBoundaryStationTarget(
                        Point2d endpoint,
                        Point2d innerEndpoint,
                        bool lineIsHorizontal,
                        LsdQuarterContext context,
                        out Point2d target)
                    {
                        return TryFindBoundaryStationTargetInContext(
                            endpoint,
                            innerEndpoint,
                            lineIsHorizontal,
                            context,
                            secBoundaryKinds,
                            out target);
                    }

                    bool ShouldUseSurveySecTargetForOuterEndpoint(
                        Point2d endpoint,
                        Point2d innerEndpoint,
                        bool lineIsHorizontal,
                        LsdQuarterContext context,
                        IReadOnlyList<string> preferredKinds)
                    {
                        var hasProjectedZeroCandidate = HasProjectedRoadAllowanceCandidateAtStation(
                            endpoint,
                            innerEndpoint,
                            lineIsHorizontal,
                            context,
                            boundaryKindZero);
                        var hasProjectedTwentyCandidate = HasProjectedRoadAllowanceCandidateAtStation(
                            endpoint,
                            innerEndpoint,
                            lineIsHorizontal,
                            context,
                            boundaryKindTwenty);
                        var hasProjectedCorrectionZeroCandidate = HasProjectedRoadAllowanceCandidateAtStation(
                            endpoint,
                            innerEndpoint,
                            lineIsHorizontal,
                            context,
                            boundaryKindCorrectionZero);

                        return SurveySecRoadAllowanceSecTargetPolicy.ShouldUseSecTarget(
                            preferredKinds,
                            hasProjectedZeroCandidate,
                            hasProjectedTwentyCandidate,
                            hasProjectedCorrectionZeroCandidate);
                    }

                    bool TryFindSouthQuarterCorrectionCorridorTargetFromLiveQsec(
                        Point2d endpoint,
                        Point2d innerEndpoint,
                        out Point2d target)
                    {
                        target = endpoint;
                    if (lineIsHorizontal ||
                        !IsSouthQuarter(context.Quarter) ||
                        !horizontalByKind.TryGetValue("CORR", out var correctionSegments) ||
                        correctionSegments.Count == 0)
                    {
                        return false;
                    }

                    if (!TryResolveSouthQuarterLiveQsecPoint(context, out var southQsecPoint))
                    {
                        return false;
                    }

                        const double connectTol = 1.0;
                        var corridorIndices = new HashSet<int>();
                        var queue = new Queue<int>();
                        for (var segmentIndex = 0; segmentIndex < correctionSegments.Count; segmentIndex++)
                        {
                            var seg = correctionSegments[segmentIndex];
                            if (DistancePointToSegment(southQsecPoint, seg.A, seg.B) > connectTol &&
                                seg.A.GetDistanceTo(southQsecPoint) > connectTol &&
                                seg.B.GetDistanceTo(southQsecPoint) > connectTol)
                            {
                                continue;
                            }

                            if (corridorIndices.Add(segmentIndex))
                            {
                                queue.Enqueue(segmentIndex);
                            }
                        }

                        while (queue.Count > 0)
                        {
                            var index = queue.Dequeue();
                            var seed = correctionSegments[index];
                            for (var segmentIndex = 0; segmentIndex < correctionSegments.Count; segmentIndex++)
                            {
                                if (corridorIndices.Contains(segmentIndex))
                                {
                                    continue;
                                }

                                var candidate = correctionSegments[segmentIndex];
                                var connected =
                                    seed.A.GetDistanceTo(candidate.A) <= connectTol ||
                                    seed.A.GetDistanceTo(candidate.B) <= connectTol ||
                                    seed.B.GetDistanceTo(candidate.A) <= connectTol ||
                                    seed.B.GetDistanceTo(candidate.B) <= connectTol;
                                if (!connected)
                                {
                                    continue;
                                }

                                corridorIndices.Add(segmentIndex);
                                queue.Enqueue(segmentIndex);
                            }
                        }

                        if (corridorIndices.Count == 0)
                        {
                            return false;
                        }

                        const double stationTol = 12.0;
                        const double sectionScopePadWide = 40.0;
                        const double axisTol = 20.0;
                        const double minMove = 0.005;
                        const double maxMove = 140.0;
                        const double minOutwardAdvance = 2.0;
                        var outward = endpoint - innerEndpoint;
                        var outwardLen = outward.Length;
                        if (outwardLen <= 1e-6)
                        {
                            return false;
                        }

                        var outwardDir = outward / outwardLen;
                        var found = false;
                        var bestTarget = endpoint;
                        var bestMove = double.MaxValue;
                        var bestCorridorGap = double.MaxValue;
                        foreach (var segmentIndex in corridorIndices)
                        {
                            var seg = correctionSegments[segmentIndex];
                            if (!TryIntersectInfiniteLineWithSegment(
                                    innerEndpoint,
                                    outwardDir,
                                    seg.A,
                                    seg.B,
                                    out var lineT) &&
                                !TryIntersectInfiniteLineWithBoundedSegmentExtensionForEndpointEnforcement(
                                    innerEndpoint,
                                    outwardDir,
                                    seg.A,
                                    seg.B,
                                    stationTol,
                                    out lineT))
                            {
                                continue;
                            }

                            if (lineT < minOutwardAdvance)
                            {
                                continue;
                            }

                            var candidatePoint = innerEndpoint + (outwardDir * lineT);

                            if (!IsPointWithinQuarterSectionScope(candidatePoint, context, sectionScopePadWide))
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(candidatePoint);
                            if (move <= minMove || move > maxMove)
                            {
                                continue;
                            }

                            var axisGap = Math.Abs(ProjectOnAxis(candidatePoint, context.EastUnit) - ProjectOnAxis(endpoint, context.EastUnit));
                            if (axisGap > axisTol)
                            {
                                continue;
                            }

                            var corridorGap = candidatePoint.GetDistanceTo(southQsecPoint);
                            if (!found ||
                                move < (bestMove - 1e-6) ||
                                (Math.Abs(move - bestMove) <= 1e-6 && corridorGap < bestCorridorGap - 1e-6))
                            {
                                found = true;
                                bestTarget = candidatePoint;
                                bestMove = move;
                                bestCorridorGap = corridorGap;
                            }
                        }

                        if (!found)
                        {
                            return false;
                        }

                        target = bestTarget;
                        return true;
                    }

                    var movedByStationTarget = false;
                    var movedByFallbackAnchor = false;
                    var usedStationTarget = false;
                    var usedSurveySecTarget = false;
                    var usedCorrectionBandShiftTarget = false;
                    var usedResolvedCorrectionSouthBoundaryTarget = false;
                    var usedResolvedCorrectionSouthHalfMidTarget = false;
                    var usedResolvedCorrectionZeroTarget = false;
                    var usedInteriorCorrectionZeroTarget = false;
                    var usedMidpointCorrectionZeroTarget = false;
                    var outerTarget = outerPoint;
                    var foundOuterTarget = false;
                    var useQuarterCorrectionZeroTarget =
                        !lineIsHorizontal &&
                        correctionOverride;
                    var useQuarterResolvedCorrectionZeroFallback =
                        useQuarterCorrectionZeroTarget &&
                        IsQuarterCorrectionSouthStationCompatible(innerPoint, context);
                    if (useQuarterResolvedCorrectionZeroFallback)
                    {
                        foundOuterTarget = TryFindQuarterCorrectionBandShiftTarget(
                            outerPoint,
                            innerPoint,
                            context,
                            out outerTarget);
                        usedCorrectionBandShiftTarget = foundOuterTarget;
                        if (!foundOuterTarget)
                        {
                            foundOuterTarget = TryFindPreferredQuarterCorrectionSouthTarget(
                                outerPoint,
                                innerPoint,
                                context,
                                out outerTarget,
                                out usedResolvedCorrectionSouthBoundaryTarget,
                                out usedResolvedCorrectionZeroTarget,
                                out usedInteriorCorrectionZeroTarget);
                        }
                        if (foundOuterTarget)
                        {
                            usedStationTarget = true;
                        }
                    }

                    if (!foundOuterTarget &&
                        !lineIsHorizontal &&
                        correctionOverride &&
                        IsSouthQuarter(context.Quarter) &&
                        TryFindQuarterResolvedCorrectionSouthHalfMidTarget(
                            outerPoint,
                            innerPoint,
                            context,
                            out outerTarget))
                    {
                        foundOuterTarget = true;
                        usedStationTarget = true;
                        usedResolvedCorrectionSouthHalfMidTarget = true;
                    }

                    if (useQuarterCorrectionZeroTarget)
                    {
                        if (!foundOuterTarget && !useQuarterResolvedCorrectionZeroFallback)
                        {
                            foundOuterTarget = TryFindQuarterCorrectionZeroMidpointTarget(
                                outerPoint,
                                innerPoint,
                                context,
                                out outerTarget);
                            usedMidpointCorrectionZeroTarget = foundOuterTarget;

                            if (foundOuterTarget)
                            {
                                usedStationTarget = true;
                            }
                        }
                    }

                    if (!correctionOverride)
                    {
                        usedSurveySecTarget = ShouldUseSurveySecTargetForOuterEndpoint(
                            outerPoint,
                            innerPoint,
                            lineIsHorizontal,
                            context,
                            preferredKinds);
                    }

                    if (!foundOuterTarget &&
                        !usedSurveySecTarget &&
                        preferredKinds.Count > 0 &&
                        string.Equals(preferredKinds[0], boundaryKindCorrection, StringComparison.OrdinalIgnoreCase) &&
                        TryFindSouthQuarterCorrectionCorridorTargetFromLiveQsec(
                            outerPoint,
                            innerPoint,
                            out outerTarget))
                    {
                        foundOuterTarget = true;
                        usedStationTarget = true;
                    }

                    if (!foundOuterTarget &&
                        usedSurveySecTarget &&
                        TryFindSecBoundaryStationTarget(
                            outerPoint,
                            innerPoint,
                            lineIsHorizontal,
                            context,
                            out outerTarget))
                    {
                        foundOuterTarget = true;
                        usedStationTarget = true;
                    }

                    if (!foundOuterTarget && !usedSurveySecTarget)
                    {
                        foundOuterTarget = TryFindBoundaryStationTargetInContext(
                            outerPoint,
                            innerPoint,
                            lineIsHorizontal,
                            context,
                            preferredKinds,
                            out outerTarget);
                        usedStationTarget = foundOuterTarget;
                    }

                    if (!foundOuterTarget &&
                        !usedSurveySecTarget &&
                        !correctionOverride &&
                        TryFindSecBoundaryStationTarget(
                            outerPoint,
                            innerPoint,
                            lineIsHorizontal,
                            context,
                            out var surveySecFallbackTarget))
                    {
                        outerTarget = surveySecFallbackTarget;
                        foundOuterTarget = true;
                        usedStationTarget = true;
                    }

                    if (foundOuterTarget &&
                        !lineIsHorizontal &&
                        correctionOverride &&
                        usedStationTarget &&
                        !usedCorrectionBandShiftTarget &&
                        !usedResolvedCorrectionSouthBoundaryTarget &&
                        !usedResolvedCorrectionSouthHalfMidTarget &&
                        !usedResolvedCorrectionZeroTarget &&
                        !usedInteriorCorrectionZeroTarget &&
                        !usedMidpointCorrectionZeroTarget &&
                        TrySnapCorrectionZeroTargetToInsetMidpoint(
                            outerTarget,
                            outerPoint,
                            innerPoint,
                            context,
                            out var insetMidpointTarget))
                    {
                        outerTarget = insetMidpointTarget;
                    }

                    if (!foundOuterTarget && useQuarterResolvedCorrectionZeroFallback)
                    {
                        foundOuterTarget = TryFindPreferredQuarterCorrectionSouthTarget(
                            outerPoint,
                            innerPoint,
                            context,
                            out outerTarget,
                            out usedResolvedCorrectionSouthBoundaryTarget,
                            out usedResolvedCorrectionZeroTarget,
                            out usedInteriorCorrectionZeroTarget);
                        usedStationTarget = foundOuterTarget;
                    }

                    if (!foundOuterTarget)
                    {
                        if (!lineIsHorizontal && correctionOverride)
                        {
                            preferredKinds.Clear();
                            preferredKinds.AddRange(BuildFallbackOuterKinds(lineIsHorizontal, context));
                            if (traceRuleFlow)
                            {
                                logger?.WriteLine(
                                    $"LSD-ENDPT line={lineIdText} pass=rule-matrix correction-override-downgraded sec={context.SectionNumber} q={context.Quarter}.");
                            }
                        }

                        foundOuterTarget = TryFindBoundaryStationTargetInContext(
                            outerPoint,
                            innerPoint,
                            lineIsHorizontal,
                            context,
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
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix outer source={(usedSurveySecTarget ? "survey-sec-target" : "fallback-anchor")} moved={movedByFallbackAnchor} target={FormatLsdEndpointTracePoint(outerTarget)} kinds={string.Join("/", preferredKinds)}.");
                        }

                        if (traceRuleFlow && TryReadOpenSegmentForEndpointEnforcement(writable, out var fallbackP0, out var fallbackP1))
                        {
                            logger?.WriteLine(
                                $"LSD-ENDPT line={lineIdText} pass=rule-matrix final p0={FormatLsdEndpointTracePoint(fallbackP0)} p1={FormatLsdEndpointTracePoint(fallbackP1)}.");
                        }

                        continue;
                    }

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
                            usedSurveySecTarget ? "survey-sec-target" :
                            usedCorrectionBandShiftTarget ? "corr-band-shift" :
                            usedResolvedCorrectionSouthBoundaryTarget ? "corr-south-hard" :
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

        private static void EnforceRegularBoundaryLsdMidpointsAfterRuleMatrix(
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

            var coreClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 0.0);
            if (coreClipWindows.Count == 0)
            {
                coreClipWindows = clipWindows;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);

            bool DoesSegmentIntersectAnyCoreWindow(Point2d a, Point2d b)
            {
                for (var i = 0; i < coreClipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, coreClipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

            bool IsRegularUsecZeroBoundaryLayer(string layer) =>
                string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase);

            bool IsRegularUsecTwentyBoundaryLayer(string layer) =>
                string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var horizontalMidpointTargetSegments = new List<(Point2d A, Point2d B, Point2d Mid, int Priority)>();
                var verticalMidpointTargetSegments = new List<(Point2d A, Point2d B, Point2d Mid, int Priority)>();
                var lsdLineIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                void AddRegularBoundaryMidpointTargetSegment(Point2d start, Point2d end, int priority)
                {
                    var midpoint = Midpoint(start, end);
                    if (IsHorizontalLikeForEndpointEnforcement(start, end))
                    {
                        horizontalMidpointTargetSegments.Add((start, end, midpoint, Priority: priority));
                    }
                    else if (IsVerticalLikeForEndpointEnforcement(start, end))
                    {
                        verticalMidpointTargetSegments.Add((start, end, midpoint, Priority: priority));
                    }
                }

                bool TryGetRegularBoundaryMidpointPriority(string layer, out int priority)
                {
                    if (IsRegularUsecTwentyBoundaryLayer(layer))
                    {
                        priority = 1;
                        return true;
                    }

                    if (IsRegularUsecZeroBoundaryLayer(layer) ||
                        string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                    {
                        priority = 2;
                        return true;
                    }

                    priority = 0;
                    return false;
                }

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var hasPrimarySegment = TryReadOpenSegmentForEndpointEnforcement(ent, out var primaryA, out var primaryB);
                    var entitySegments = CollectEntitySegmentsForEndpointEnforcement(ent, hasPrimarySegment, primaryA, primaryB);

                    if (entitySegments.Count == 0)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) &&
                        hasPrimarySegment &&
                        IsAdjustableLsdLineSegment(primaryA, primaryB) &&
                        DoesSegmentIntersectAnyCoreWindow(primaryA, primaryB))
                    {
                        lsdLineIds.Add(id);
                        continue;
                    }

                    for (var si = 0; si < entitySegments.Count; si++)
                    {
                        var seg = entitySegments[si];
                        if (!DoesSegmentIntersectAnyWindow(seg.A, seg.B))
                        {
                            continue;
                        }

                        if (TryGetRegularBoundaryMidpointPriority(layer, out var priority))
                        {
                            AddRegularBoundaryMidpointTargetSegment(seg.A, seg.B, priority);
                        }
                    }
                }

                if ((horizontalMidpointTargetSegments.Count == 0 && verticalMidpointTargetSegments.Count == 0) ||
                    lsdLineIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointMoveTol = 0.005;
                const double endpointOnSegmentTol = 0.75;
                const double endpointAxisTol = 2.00;
                const double endpointSpanTol = 2.00;
                const double maxMidpointShift = 1200.0;
                const double minRemainingLength = 2.0;
                var scannedLines = 0;
                var endpointCandidates = 0;
                var alreadyAtMidpoint = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;

                bool TryFindRegularBoundaryMidpoint(
                    IReadOnlyList<(Point2d A, Point2d B, Point2d Mid, int Priority)> targetSegments,
                    Point2d endpoint,
                    bool horizontalTargets,
                    out Point2d target)
                {
                    target = endpoint;
                    var found = false;
                    var bestPriority = int.MaxValue;
                    var bestSegDistance = double.MaxValue;
                    var bestAxisGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < targetSegments.Count; i++)
                    {
                        var seg = targetSegments[i];
                        var segDistance = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (segDistance > endpointOnSegmentTol)
                        {
                            continue;
                        }

                        var projectedAxisValue = horizontalTargets
                            ? 0.5 * (seg.A.Y + seg.B.Y)
                            : 0.5 * (seg.A.X + seg.B.X);
                        var primarySpan = horizontalTargets
                            ? seg.B.X - seg.A.X
                            : seg.B.Y - seg.A.Y;
                        if (Math.Abs(primarySpan) > 1e-6)
                        {
                            var t = horizontalTargets
                                ? (endpoint.X - seg.A.X) / primarySpan
                                : (endpoint.Y - seg.A.Y) / primarySpan;
                            if (t < 0.0) t = 0.0;
                            if (t > 1.0) t = 1.0;
                            projectedAxisValue = horizontalTargets
                                ? seg.A.Y + ((seg.B.Y - seg.A.Y) * t)
                                : seg.A.X + ((seg.B.X - seg.A.X) * t);
                        }

                        var axisGap = horizontalTargets
                            ? Math.Abs(endpoint.Y - projectedAxisValue)
                            : Math.Abs(endpoint.X - projectedAxisValue);
                        if (axisGap > endpointAxisTol)
                        {
                            continue;
                        }

                        var endpointSpanValue = horizontalTargets ? endpoint.X : endpoint.Y;
                        var spanMin = horizontalTargets
                            ? Math.Min(seg.A.X, seg.B.X)
                            : Math.Min(seg.A.Y, seg.B.Y);
                        var spanMax = horizontalTargets
                            ? Math.Max(seg.A.X, seg.B.X)
                            : Math.Max(seg.A.Y, seg.B.Y);
                        if (endpointSpanValue < (spanMin - endpointSpanTol) ||
                            endpointSpanValue > (spanMax + endpointSpanTol))
                        {
                            continue;
                        }

                        var move = endpoint.GetDistanceTo(seg.Mid);
                        if (move > maxMidpointShift)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            seg.Priority < bestPriority ||
                            (seg.Priority == bestPriority && segDistance < (bestSegDistance - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(segDistance - bestSegDistance) <= 1e-6 && axisGap < (bestAxisGap - 1e-6)) ||
                            (seg.Priority == bestPriority && Math.Abs(segDistance - bestSegDistance) <= 1e-6 && Math.Abs(axisGap - bestAxisGap) <= 1e-6 && move < bestMove);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestPriority = seg.Priority;
                        bestSegDistance = segDistance;
                        bestAxisGap = axisGap;
                        bestMove = move;
                        target = seg.Mid;
                    }

                    return found;
                }

                bool TryFindRegularHorizontalBoundaryMidpoint(Point2d endpoint, out Point2d target) =>
                    TryFindRegularBoundaryMidpoint(horizontalMidpointTargetSegments, endpoint, horizontalTargets: true, out target);

                bool TryFindRegularVerticalBoundaryMidpoint(Point2d endpoint, out Point2d target) =>
                    TryFindRegularBoundaryMidpoint(verticalMidpointTargetSegments, endpoint, horizontalTargets: false, out target);

                bool TryPlanRegularBoundaryMidpointMove(Point2d endpoint, bool verticalLine, out Point2d target)
                {
                    target = endpoint;
                    var found = verticalLine
                        ? TryFindRegularHorizontalBoundaryMidpoint(endpoint, out target)
                        : TryFindRegularVerticalBoundaryMidpoint(endpoint, out target);
                    if (!found)
                    {
                        return false;
                    }

                    endpointCandidates++;
                    if (endpoint.GetDistanceTo(target) <= endpointMoveTol)
                    {
                        alreadyAtMidpoint++;
                        return false;
                    }

                    return true;
                }

                bool TryApplyRegularBoundaryMidpointMove(
                    Entity writable,
                    ObjectId lineId,
                    bool moveStart,
                    Point2d target,
                    ref Point2d start,
                    ref Point2d end,
                    out bool segmentReadable)
                {
                    segmentReadable = true;
                    var oppositeEndpoint = moveStart ? end : start;
                    if (target.GetDistanceTo(oppositeEndpoint) < minRemainingLength ||
                        !TryMoveEndpoint(writable, moveStart, target, endpointMoveTol))
                    {
                        return false;
                    }

                    adjustedEndpoints++;
                    logger?.WriteLine(
                        $"LSD-ENDPT regular-post line={FormatLsdEndpointTraceId(lineId)} endpoint={(moveStart ? "start" : "end")} target={FormatLsdEndpointTracePoint(target)}.");
                    if (!moveStart)
                    {
                        return true;
                    }

                    segmentReadable = TryReadOpenSegmentForEndpointEnforcement(writable, out start, out end);
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

                    var isVertical = IsVerticalLikeForEndpointEnforcement(p0, p1);
                    var isHorizontal = IsHorizontalLikeForEndpointEnforcement(p0, p1);
                    if (!isVertical && !isHorizontal)
                    {
                        continue;
                    }

                    scannedLines++;
                    var movePlan = EndpointMovePlan.Create(p0, p1);
                    if (TryPlanRegularBoundaryMidpointMove(p0, isVertical, out var startTarget))
                    {
                        movePlan.MoveStart = true;
                        movePlan.TargetStart = startTarget;
                    }

                    if (TryPlanRegularBoundaryMidpointMove(p1, isVertical, out var endTarget))
                    {
                        movePlan.MoveEnd = true;
                        movePlan.TargetEnd = endTarget;
                    }

                    if (!movePlan.MoveStart && !movePlan.MoveEnd)
                    {
                        continue;
                    }

                    var movedLine = false;
                    if (movePlan.MoveStart &&
                        TryApplyRegularBoundaryMidpointMove(
                            writable,
                            id,
                            moveStart: true,
                            movePlan.TargetStart,
                            ref p0,
                            ref p1,
                            out var segmentReadable))
                    {
                        movedLine = true;
                        if (!segmentReadable)
                        {
                            continue;
                        }
                    }

                    if (movePlan.MoveEnd &&
                        TryApplyRegularBoundaryMidpointMove(
                            writable,
                            id,
                            moveStart: false,
                            movePlan.TargetEnd,
                            ref p0,
                            ref p1,
                            out _))
                    {
                        movedLine = true;
                    }

                    if (movedLine)
                    {
                        adjustedLines++;
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: regular-boundary LSD midpoint post-pass scannedLines={scannedLines}, endpointCandidates={endpointCandidates}, alreadyAtMidpoint={alreadyAtMidpoint}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
            }
        }

        private static void EnforceVerticalBlindBoundaryLsdMidpointsAfterRuleMatrix(
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

            var coreClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 0.0);
            if (coreClipWindows.Count == 0)
            {
                coreClipWindows = clipWindows;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForEndpointEnforcement(a, b, clipWindows);

            bool DoesSegmentIntersectAnyCoreWindow(Point2d a, Point2d b)
            {
                for (var i = 0; i < coreClipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, coreClipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForEndpointEnforcement(writable, moveStart, target, moveTol);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var horizontalBlindSegments = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var lsdLineIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                void AddBlindBoundaryMidpointTargetSegment(Point2d start, Point2d end)
                {
                    if (IsHorizontalLikeForEndpointEnforcement(start, end))
                    {
                        horizontalBlindSegments.Add((start, end, Midpoint(start, end)));
                    }
                }

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var hasPrimarySegment = TryReadOpenSegmentForEndpointEnforcement(ent, out var primaryA, out var primaryB);
                    var entitySegments = CollectEntitySegmentsForEndpointEnforcement(ent, hasPrimarySegment, primaryA, primaryB);

                    if (entitySegments.Count == 0)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) &&
                        hasPrimarySegment &&
                        IsAdjustableLsdLineSegment(primaryA, primaryB) &&
                        DoesSegmentIntersectAnyCoreWindow(primaryA, primaryB))
                    {
                        lsdLineIds.Add(id);
                        continue;
                    }

                    if (!string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    for (var si = 0; si < entitySegments.Count; si++)
                    {
                        var seg = entitySegments[si];
                        if (!DoesSegmentIntersectAnyWindow(seg.A, seg.B) ||
                            !IsHorizontalLikeForEndpointEnforcement(seg.A, seg.B))
                        {
                            continue;
                        }

                        AddBlindBoundaryMidpointTargetSegment(seg.A, seg.B);
                    }
                }

                if (horizontalBlindSegments.Count == 0 || lsdLineIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointMoveTol = 0.005;
                const double endpointOnSegmentTol = 0.75;
                const double endpointYLineTol = 2.00;
                const double xSpanTol = 2.00;
                const double maxMidpointShift = 1200.0;
                const double minRemainingLength = 2.0;
                var scannedLines = 0;
                var blindEndpointCandidates = 0;
                var alreadyAtMidpoint = 0;
                var adjustedEndpoints = 0;
                var adjustedLines = 0;
                var ambiguousLines = 0;

                bool TryFindBlindBoundaryMidpoint(Point2d endpoint, out Point2d target)
                {
                    target = endpoint;
                    var found = false;
                    var bestSegDistance = double.MaxValue;
                    var bestYGap = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var i = 0; i < horizontalBlindSegments.Count; i++)
                    {
                        var seg = horizontalBlindSegments[i];
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
                        if (move > maxMidpointShift)
                        {
                            continue;
                        }

                        var better =
                            !found ||
                            segDistance < (bestSegDistance - 1e-6) ||
                            (Math.Abs(segDistance - bestSegDistance) <= 1e-6 && yGap < (bestYGap - 1e-6)) ||
                            (Math.Abs(segDistance - bestSegDistance) <= 1e-6 && Math.Abs(yGap - bestYGap) <= 1e-6 && move < bestMove);
                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestSegDistance = segDistance;
                        bestYGap = yGap;
                        bestMove = move;
                        target = seg.Mid;
                    }

                    return found;
                }

                bool TryPlanBlindBoundaryMidpointMove(Point2d endpoint, out Point2d target)
                {
                    target = endpoint;
                    if (!TryFindBlindBoundaryMidpoint(endpoint, out target))
                    {
                        return false;
                    }

                    blindEndpointCandidates++;
                    if (endpoint.GetDistanceTo(target) <= endpointMoveTol)
                    {
                        alreadyAtMidpoint++;
                        return false;
                    }

                    return true;
                }

                bool TryApplyBlindBoundaryMidpointMove(
                    Entity writable,
                    ObjectId lineId,
                    bool moveStart,
                    Point2d target,
                    Point2d otherEndpoint)
                {
                    if (target.GetDistanceTo(otherEndpoint) < minRemainingLength ||
                        !TryMoveEndpoint(writable, moveStart, target, endpointMoveTol))
                    {
                        return false;
                    }

                    adjustedEndpoints++;
                    adjustedLines++;
                    logger?.WriteLine(
                        $"LSD-ENDPT blind-post line={FormatLsdEndpointTraceId(lineId)} endpoint={(moveStart ? "start" : "end")} target={FormatLsdEndpointTracePoint(target)}.");
                    return true;
                }

                for (var i = 0; i < lsdLineIds.Count; i++)
                {
                    var id = lsdLineIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForEndpointEnforcement(writable, out var p0, out var p1) ||
                        !IsVerticalLikeForEndpointEnforcement(p0, p1))
                    {
                        continue;
                    }

                    scannedLines++;
                    var hasStartTarget = TryPlanBlindBoundaryMidpointMove(p0, out var targetStart);
                    var hasEndTarget = TryPlanBlindBoundaryMidpointMove(p1, out var targetEnd);

                    if (hasStartTarget && hasEndTarget)
                    {
                        ambiguousLines++;
                        continue;
                    }

                    if (!hasStartTarget && !hasEndTarget)
                    {
                        continue;
                    }

                    var moveStart = hasStartTarget;
                    var target = hasStartTarget ? targetStart : targetEnd;
                    var other = hasStartTarget ? p1 : p0;
                    TryApplyBlindBoundaryMidpointMove(writable, id, moveStart, target, other);
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: blind-boundary LSD midpoint post-pass scannedLines={scannedLines}, blindEndpointCandidates={blindEndpointCandidates}, alreadyAtMidpoint={alreadyAtMidpoint}, ambiguousLines={ambiguousLines}, adjustedEndpoints={adjustedEndpoints}, adjustedLines={adjustedLines}.");
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
                EnforceRegularBoundaryLsdMidpointsAfterRuleMatrix(database, requestedQuarterIds, logger);
                EnforceVerticalBlindBoundaryLsdMidpointsAfterRuleMatrix(database, requestedQuarterIds, logger);
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
                    var layer = ent.Layer ?? string.Empty;
                    var isCorrectionLayer =
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
                    var scopedSegments = CollectScopedEntitySegmentsForEndpointEnforcement(
                        ent,
                        hasPrimarySegment,
                        primaryA,
                        primaryB,
                        DoesSegmentIntersectAnyWindow,
                        // Keep correction boundaries available even if they sit just outside the
                        // clipped request window; LSD correction snap can require adjacent seam rows.
                        fallbackToAllSegments: isCorrectionLayer);
                    if (scopedSegments.Count == 0)
                    {
                        continue;
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

                bool IsBoundarySegmentOrthogonalToLsdSource(
                    Point2d segA,
                    Point2d segB,
                    bool sourceHorizontal,
                    bool sourceVertical)
                {
                    var segHorizontal = IsHorizontalLikeForEndpointEnforcement(segA, segB);
                    var segVertical = IsVerticalLikeForEndpointEnforcement(segA, segB);
                    if (sourceHorizontal && !segVertical)
                    {
                        return false;
                    }

                    if (sourceVertical && !segHorizontal)
                    {
                        return false;
                    }

                    return true;
                }

                bool TryGetLsdSourceOrientation(
                    Point2d endpoint,
                    Point2d other,
                    out bool sourceHorizontal,
                    out bool sourceVertical)
                {
                    sourceHorizontal = IsHorizontalLikeForEndpointEnforcement(other, endpoint);
                    sourceVertical = IsVerticalLikeForEndpointEnforcement(other, endpoint);
                    return sourceHorizontal || sourceVertical;
                }

                bool TryGetLsdEndpointSnapDirections(
                    Point2d endpoint,
                    Point2d other,
                    out bool sourceHorizontal,
                    out bool sourceVertical,
                    out Vector2d outwardDir,
                    out double outwardLen,
                    out Vector2d perpDir)
                {
                    sourceHorizontal = false;
                    sourceVertical = false;
                    outwardDir = default;
                    perpDir = default;

                    var outward = endpoint - other;
                    outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        return false;
                    }

                    if (!TryGetLsdSourceOrientation(endpoint, other, out sourceHorizontal, out sourceVertical))
                    {
                        return false;
                    }

                    outwardDir = outward / outwardLen;
                    perpDir = new Vector2d(-outwardDir.Y, outwardDir.X);
                    return true;
                }

                bool TryFindSnapTarget(Point2d endpoint, Point2d other, out Point2d target)
                {
                    target = endpoint;
                    if (!TryGetLsdEndpointSnapDirections(
                            endpoint,
                            other,
                            out var sourceHorizontal,
                            out var sourceVertical,
                            out var outwardDir,
                            out _,
                            out var perpDir))
                    {
                        return false;
                    }

                    var found = false;
                    var bestScore = double.MaxValue;
                    var bestTarget = endpoint;
                    for (var i = 0; i < hardBoundarySegments.Count; i++)
                    {
                        var seg = hardBoundarySegments[i];
                        // LSD endpoints should land on midpoint of orthogonal hard boundary.
                        if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
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
                    if (!TryGetLsdEndpointSnapDirections(
                            endpoint,
                            other,
                            out var sourceHorizontal,
                            out var sourceVertical,
                            out var outwardDir,
                            out var outwardLen,
                            out var perpDir))
                    {
                        return false;
                    }

                    var traceThisEndpoint = logger != null;

                    var found = false;
                    var bestPrimaryScore = double.MaxValue;
                    var bestAlongFromOther = double.MaxValue;
                    var bestLateral = double.MaxValue;
                    var bestMove = double.MaxValue;
                    var bestTarget = endpoint;
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
                            if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
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
                            if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
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
                        if (!IsBoundarySegmentOrthogonalToLsdSource(a, b, sourceHorizontal, sourceVertical))
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

            bool TrySelectPreferredHardBoundaryTarget(
                Point2d defaultTarget,
                bool? preferZero,
                Func<(Point2d A, Point2d B, bool IsZero), (bool Accepted, Point2d Target, double Score)> tryBuildCandidate,
                out Point2d target)
            {
                target = defaultTarget;
                var foundPreferred = false;
                var bestPreferredScore = double.MaxValue;
                var bestPreferredTarget = defaultTarget;
                var foundFallback = false;
                var bestFallbackScore = double.MaxValue;
                var bestFallbackTarget = defaultTarget;

                for (var i = 0; i < hardBoundarySegments.Count; i++)
                {
                    var seg = hardBoundarySegments[i];
                    var candidate = tryBuildCandidate(seg);
                    if (!candidate.Accepted)
                    {
                        continue;
                    }

                    var isPreferred = !preferZero.HasValue || seg.IsZero == preferZero.Value;
                    if (isPreferred)
                    {
                        if (candidate.Score >= bestPreferredScore)
                        {
                            continue;
                        }

                        bestPreferredScore = candidate.Score;
                        bestPreferredTarget = candidate.Target;
                        foundPreferred = true;
                    }
                    else
                    {
                        if (candidate.Score >= bestFallbackScore)
                        {
                            continue;
                        }

                        bestFallbackScore = candidate.Score;
                        bestFallbackTarget = candidate.Target;
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
                    if (!TryGetLsdEndpointSnapDirections(
                            endpoint,
                            other,
                            out var sourceHorizontal,
                            out var sourceVertical,
                            out var outwardDir,
                            out _,
                            out var perpDir))
                    {
                        return false;
                    }

                    var maxCandidateMove = maxMoveOverride ?? maxMove;
                    return TrySelectPreferredHardBoundaryTarget(
                        endpoint,
                        preferZero,
                        seg =>
                        {
                            if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var midpoint = Midpoint(seg.A, seg.B);
                            var delta = midpoint - endpoint;
                            var move = delta.Length;
                            if (move <= minMove || move > maxCandidateMove)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var lateral = Math.Abs(delta.DotProduct(perpDir));
                            if (lateral > midpointAxisTol)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var projectedFromOther = (midpoint - other).DotProduct(outwardDir);
                            if (projectedFromOther < minRemainingLength)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var score = (lateral * 100.0) + move;
                            return (Accepted: true, Target: midpoint, Score: score);
                        },
                        out target);
                }

                bool IsEndpointOnCorrectionOuterBoundary(Point2d endpoint)
                    => IsEndpointOnBoundarySegmentsForEndpointEnforcement(endpoint, correctionOuterBoundarySegments, endpointTouchTol);

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
                    if (!TryGetLsdSourceOrientation(endpoint, other, out var sourceHorizontal, out var sourceVertical))
                    {
                        return false;
                    }

                    const double relaxedAxisTol = 80.0;
                    var maxCandidateMove = maxMoveOverride ?? maxMove;
                    return TrySelectPreferredHardBoundaryTarget(
                        endpoint,
                        preferZero,
                        seg =>
                        {
                            if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var midpoint = Midpoint(seg.A, seg.B);
                            var dx = midpoint.X - endpoint.X;
                            var dy = midpoint.Y - endpoint.Y;
                            var move = Math.Sqrt((dx * dx) + (dy * dy));
                            if (move <= minMove || move > maxCandidateMove)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var axisGap = sourceHorizontal ? Math.Abs(dy) : Math.Abs(dx);
                            if (axisGap > relaxedAxisTol)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var score = (axisGap * 100.0) + move;
                            return (Accepted: true, Target: midpoint, Score: score);
                        },
                        out target);
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
                    if (!TryGetLsdEndpointSnapDirections(
                            endpoint,
                            other,
                            out var sourceHorizontal,
                            out var sourceVertical,
                            out var outwardDir,
                            out _,
                            out var perpDir))
                    {
                        return false;
                    }

                    var lateralTol = lateralTolOverride ?? midpointAxisTol;
                    var maxCandidateMove = maxMoveOverride ?? maxMove;

                    Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b) =>
                        ClosestPointOnSegmentForEndpointEnforcement(p, a, b);

                    return TrySelectPreferredHardBoundaryTarget(
                        endpoint,
                        preferZero,
                        seg =>
                        {
                            if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var candidate = ClosestPointOnSegment(endpoint, seg.A, seg.B);
                            var delta = candidate - endpoint;
                            var move = delta.Length;
                            if (move <= minMove || move > maxCandidateMove)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var lateral = Math.Abs(delta.DotProduct(perpDir));
                            if (lateral > lateralTol)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var projectedFromOther = (candidate - other).DotProduct(outwardDir);
                            if (!allowBacktrack && projectedFromOther < minRemainingLength)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var score = (lateral * 100.0) + move;
                            return (Accepted: true, Target: candidate, Score: score);
                        },
                        out target);
                }

                // Fallback for non-correction endpoints that are already touching a hard boundary
                // but are not yet on that segment midpoint.
                bool TryFindCurrentHardBoundaryMidpoint(Point2d endpoint, Point2d other, bool? preferZero, out Point2d target)
                {
                    target = endpoint;
                    if (!TryGetLsdSourceOrientation(endpoint, other, out var sourceHorizontal, out var sourceVertical))
                    {
                        return false;
                    }

                    return TrySelectPreferredHardBoundaryTarget(
                        endpoint,
                        preferZero,
                        seg =>
                        {
                            if (!IsBoundarySegmentOrthogonalToLsdSource(seg.A, seg.B, sourceHorizontal, sourceVertical))
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            if (DistancePointToSegment(endpoint, seg.A, seg.B) > endpointTouchTol)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            var midpoint = Midpoint(seg.A, seg.B);
                            var move = endpoint.GetDistanceTo(midpoint);
                            if (move <= minMove || move > maxMove)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            if (midpoint.GetDistanceTo(other) < minRemainingLength)
                            {
                                return (Accepted: false, Target: endpoint, Score: double.MaxValue);
                            }

                            return (Accepted: true, Target: midpoint, Score: move);
                        },
                        out target);
                }

                bool TryFindThirtyBoundaryFallbackTarget(
                    Point2d endpoint,
                    Point2d other,
                    out Point2d snappedTarget,
                    out string sourceTag)
                {
                    snappedTarget = endpoint;
                    sourceTag = "none";

                    bool? preferZero = null;
                    var sourceIsHorizontalLsd = IsHorizontalLikeForEndpointEnforcement(endpoint, other);
                    var sourceIsVerticalLsd = IsVerticalLikeForEndpointEnforcement(endpoint, other);
                    if (sourceIsHorizontalLsd)
                    {
                        // Horizontal LSD side rule: right endpoint -> 0, left endpoint -> 20.12.
                        preferZero = endpoint.X > other.X;
                    }
                    else if (sourceIsVerticalLsd)
                    {
                        // Vertical LSD side rule: north endpoint -> 0, south endpoint -> 20.12.
                        preferZero = endpoint.Y > other.Y;
                    }

                    var foundTarget = false;
                    if (sourceIsHorizontalLsd || sourceIsVerticalLsd)
                    {
                        foundTarget = TryFindPreferredHardBoundaryMidpoint(
                                endpoint,
                                other,
                                preferZero,
                                out snappedTarget,
                                maxMoveOverride: thirtyEscapeMaxMove) ||
                            TryFindPreferredHardBoundaryMidpointRelaxed(
                                endpoint,
                                other,
                                preferZero,
                                out snappedTarget,
                                maxMoveOverride: thirtyEscapeMaxMove) ||
                            TryFindNearestHardBoundaryPoint(
                                endpoint,
                                other,
                                preferZero,
                                out snappedTarget) ||
                            TryFindNearestHardBoundaryPoint(
                                endpoint,
                                other,
                                preferZero,
                                out snappedTarget,
                                lateralTolOverride: thirtyEscapeLateralTol,
                                maxMoveOverride: thirtyEscapeMaxMove,
                                allowBacktrack: true);
                        if (foundTarget)
                        {
                            sourceTag = "thirty-fallback-chain-axis";
                            return true;
                        }
                    }
                    else
                    {
                        foundTarget =
                            // Generic fallback for non-axis LSD segments.
                            TryFindNearestUsecMidpoint(endpoint, preferZero, out snappedTarget) ||
                            TryFindNearestHardBoundaryPoint(endpoint, other, preferZero, out snappedTarget) ||
                            TryFindNearestHardBoundaryPoint(
                                endpoint,
                                other,
                                preferZero,
                                out snappedTarget,
                                lateralTolOverride: thirtyEscapeLateralTol,
                                maxMoveOverride: thirtyEscapeMaxMove,
                                allowBacktrack: true);
                        if (foundTarget)
                        {
                            sourceTag = "thirty-fallback-chain-generic";
                            return true;
                        }
                    }

                    if (TryFindSnapTarget(endpoint, other, out snappedTarget))
                    {
                        sourceTag = "generic-snaptarget";
                        return true;
                    }

                    return false;
                }

                bool IsEndpointCorrectionAdjacentForMidpoint(Point2d endpoint)
                {
                    if (!IsEndpointNearCorrectionBoundary(endpoint))
                    {
                        return false;
                    }

                    if (IsEndpointOnThirtyBoundary(endpoint) || IsEndpointOnCorrectionOuterBoundary(endpoint))
                    {
                        return true;
                    }

                    return IsEndpointOnUsecZeroBoundary(endpoint);
                }

                (bool OnZero, bool OnTwenty, bool OnThirty, bool OnCorrectionOuter, bool CorrectionAdjacent, bool CorrectionSnapEligible)
                    GetLsdEndpointBoundaryState(Point2d endpoint, Point2d other)
                {
                    var onZero = IsEndpointOnUsecZeroBoundary(endpoint);
                    var onTwenty = IsEndpointOnUsecTwentyBoundary(endpoint);
                    var onThirty = IsEndpointOnThirtyBoundary(endpoint);
                    var onCorrectionOuter = IsEndpointOnCorrectionOuterBoundary(endpoint);
                    var correctionAdjacent = IsEndpointNearCorrectionBoundary(endpoint);
                    var onCorrectionZero = onZero && correctionAdjacent;
                    var correctionSnapEligible =
                        IsVerticalLikeForEndpointEnforcement(endpoint, other) &&
                        correctionAdjacent &&
                        !onTwenty &&
                        (onThirty || onCorrectionOuter || onCorrectionZero);
                    return (onZero, onTwenty, onThirty, onCorrectionOuter, correctionAdjacent, correctionSnapEligible);
                }

                bool TryFindLsdEndpointSnapTarget(
                    Point2d endpoint,
                    Point2d other,
                    (bool OnZero, bool OnTwenty, bool OnThirty, bool OnCorrectionOuter, bool CorrectionAdjacent, bool CorrectionSnapEligible) state,
                    out Point2d snappedTarget,
                    out string sourceTag)
                {
                    snappedTarget = endpoint;
                    sourceTag = "none";

                    if (!state.CorrectionSnapEligible &&
                        (state.OnZero || state.OnTwenty) &&
                        TryFindCurrentHardBoundaryMidpoint(
                            endpoint,
                            other,
                            state.OnZero ? true : (state.OnTwenty ? false : (bool?)null),
                            out var currentBoundaryMidpoint))
                    {
                        snappedTarget = currentBoundaryMidpoint;
                        sourceTag = "current-hard-midpoint";
                        return true;
                    }

                    if (state.CorrectionSnapEligible &&
                        TryFindCorrectionAdjacentSnapTarget(
                            endpoint,
                            other,
                            state.OnCorrectionOuter,
                            out snappedTarget))
                    {
                        sourceTag = "correction-adjacent";
                        return true;
                    }

                    if (state.OnThirty)
                    {
                        return TryFindThirtyBoundaryFallbackTarget(
                            endpoint,
                            other,
                            out snappedTarget,
                            out sourceTag);
                    }

                    if (TryFindSnapTarget(endpoint, other, out snappedTarget))
                    {
                        sourceTag = "generic-snaptarget";
                        return true;
                    }

                    return false;
                }

                void FinalizeLsdEndpointResolution(
                    Point2d endpoint,
                    Point2d snappedTarget,
                    bool foundTarget,
                    bool isOnKnownUsecHardBoundary,
                    ref bool moveEndpoint,
                    ref Point2d moveTarget,
                    ref string decision,
                    ref string source)
                {
                    if (foundTarget)
                    {
                        if (endpoint.GetDistanceTo(snappedTarget) <= endpointTouchTol)
                        {
                            alreadyOnHardBoundary++;
                            decision = "already-on-target";
                            if (string.Equals(source, "none", StringComparison.OrdinalIgnoreCase))
                            {
                                source = "resolved-target";
                            }
                        }
                        else
                        {
                            moveEndpoint = true;
                            moveTarget = snappedTarget;
                            decision = "move-to-target";
                        }

                        return;
                    }

                    if (isOnKnownUsecHardBoundary || IsEndpointOnHardBoundary(endpoint))
                    {
                        alreadyOnHardBoundary++;
                        decision = "already-on-hard";
                        source = "existing-hard";
                        return;
                    }

                    noTarget++;
                    decision = "no-target";
                }

                void ResolveLsdEndpointMove(
                    Point2d endpoint,
                    Point2d other,
                    bool midpointLocked,
                    ref bool moveEndpoint,
                    ref Point2d moveTarget,
                    ref string decision,
                    ref string source)
                {
                    if (midpointLocked)
                    {
                        decision = "midpoint-locked";
                        source = "midpoint-lock";
                        return;
                    }

                    scannedEndpoints++;
                    var state = GetLsdEndpointBoundaryState(endpoint, other);
                    if (IsPointOnAnyWindowBoundary(endpoint, outerBoundaryTol) && !state.OnThirty)
                    {
                        boundarySkipped++;
                        decision = "boundary-skip";
                        source = "window-boundary";
                        return;
                    }

                    if (state.OnThirty)
                    {
                        onThirtyOnly++;
                    }

                    var foundTarget = TryFindLsdEndpointSnapTarget(
                        endpoint,
                        other,
                        state,
                        out var snappedTarget,
                        out source);
                    FinalizeLsdEndpointResolution(
                        endpoint,
                        snappedTarget,
                        foundTarget,
                        state.OnZero || state.OnTwenty,
                        ref moveEndpoint,
                        ref moveTarget,
                        ref decision,
                        ref source);
                }

                bool TryApplyLsdEndpointMove(
                    Entity writable,
                    bool moveStart,
                    bool shouldMove,
                    Point2d target,
                    ref string decision)
                {
                    if (!shouldMove)
                    {
                        return false;
                    }

                    if (!TryMoveEndpoint(writable, moveStart, target, endpointMoveTol))
                    {
                        decision = "move-attempt-failed";
                        return false;
                    }

                    adjustedEndpoints++;
                    return true;
                }

                bool TryResolveHorizontalEndpointMidpointAxisX(
                    Point2d endpoint,
                    Point2d other,
                    double targetY,
                    bool allowGenericQsecAxisFallback,
                    out double targetX)
                {
                    var probe = new Point2d(endpoint.X, targetY);
                    if (TryResolveVerticalQsecAxisXForHorizontalEndpoint(probe, other, targetY, out targetX))
                    {
                        return true;
                    }

                    if (allowGenericQsecAxisFallback && TryResolveVerticalQsecAxisX(probe, out targetX))
                    {
                        return true;
                    }

                    return TryResolveVerticalMidpointAxisX(probe, targetY, out targetX);
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

                    void PlanMidpointEndpointMove(Point2d endpoint, Point2d snappedTarget, ref bool moveEndpoint, ref Point2d moveTarget)
                    {
                        if (endpoint.GetDistanceTo(snappedTarget) > midpointEndpointMoveTol)
                        {
                            moveEndpoint = true;
                            moveTarget = snappedTarget;
                        }
                    }

                    bool TryApplyMidpointEndpointMoves()
                    {
                        if (!moveStart && !moveEnd)
                        {
                            return true;
                        }

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

                        return TryReadOpenSegmentForEndpointEnforcement(writable, out p0, out p1);
                    }

                    void ResetMidpointMoveTargets()
                    {
                        moveStart = false;
                        moveEnd = false;
                        targetStart = p0;
                        targetEnd = p1;
                    }

                    // Midpoint special case:
                    // anchor LSD endpoints to the midpoint of the line they terminate on
                    // (1/4, blind, or section) before generic hard-boundary snapping.
                    if (IsVerticalLikeForEndpointEnforcement(p0, p1))
                    {
                        var p0CorrectionAdjacentForMidpoint =
                            IsEndpointCorrectionAdjacentForMidpoint(p0);
                        var p1CorrectionAdjacentForMidpoint =
                            IsEndpointCorrectionAdjacentForMidpoint(p1);
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
                                PlanMidpointEndpointMove(p0, snappedStart, ref moveStart, ref targetStart);
                            }

                            if (hasEndMid)
                            {
                                var snappedEnd = new Point2d(midEndX, midEndY);
                                PlanMidpointEndpointMove(p1, snappedEnd, ref moveEnd, ref targetEnd);
                            }

                            if (!TryApplyMidpointEndpointMoves())
                            {
                                continue;
                            }

                            midpointLockedStart = hasStartMid &&
                                !IsEndpointCorrectionAdjacentForMidpoint(p0);
                            midpointLockedEnd = hasEndMid &&
                                !IsEndpointCorrectionAdjacentForMidpoint(p1);
                            ResetMidpointMoveTargets();
                        }
                    }
                    else if (IsHorizontalLikeForEndpointEnforcement(p0, p1))
                    {
                        var p0CorrectionAdjacentForMidpoint =
                            IsEndpointCorrectionAdjacentForMidpoint(p0);
                        var p1CorrectionAdjacentForMidpoint =
                            IsEndpointCorrectionAdjacentForMidpoint(p1);
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

                                if (TryResolveHorizontalEndpointMidpointAxisX(
                                    p0,
                                    p1,
                                    midStartY,
                                    allowGenericQsecAxisFallback: false,
                                    out var qsecAxisStartX))
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

                                if (TryResolveHorizontalEndpointMidpointAxisX(
                                    p1,
                                    p0,
                                    midEndY,
                                    allowGenericQsecAxisFallback: false,
                                    out var qsecAxisEndX))
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
                            if (TryResolveHorizontalEndpointMidpointAxisX(
                                p0,
                                p1,
                                midStartY,
                                allowGenericQsecAxisFallback: true,
                                out var axisStartX))
                            {
                                midStartX = axisStartX;
                                midStartHasExplicitX = true;
                            }
                        }

                        if (hasEndMid && !midEndHasExplicitX)
                        {
                            if (TryResolveHorizontalEndpointMidpointAxisX(
                                p1,
                                p0,
                                midEndY,
                                allowGenericQsecAxisFallback: true,
                                out var axisEndX))
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
                                PlanMidpointEndpointMove(p0, snappedStart, ref moveStart, ref targetStart);
                            }

                            if (hasEndMid)
                            {
                                var snappedEnd = new Point2d(
                                    midEndHasExplicitX ? midEndX : p1.X,
                                    midEndY);
                                PlanMidpointEndpointMove(p1, snappedEnd, ref moveEnd, ref targetEnd);
                            }

                            if (!TryApplyMidpointEndpointMoves())
                            {
                                continue;
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
                            ResetMidpointMoveTargets();
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

                    ResolveLsdEndpointMove(
                        p0,
                        p1,
                        midpointLockedStart,
                        ref moveStart,
                        ref targetStart,
                        ref startDecision,
                        ref startSource);

                    ResolveLsdEndpointMove(
                        p1,
                        p0,
                        midpointLockedEnd,
                        ref moveEnd,
                        ref targetEnd,
                        ref endDecision,
                        ref endSource);

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
                    var movedStart = TryApplyLsdEndpointMove(
                        writable,
                        moveStart: true,
                        shouldMove: moveStart,
                        target: targetStart,
                        ref startDecision);
                    var movedEnd = TryApplyLsdEndpointMove(
                        writable,
                        moveStart: false,
                        shouldMove: moveEnd,
                        target: targetEnd,
                        ref endDecision);
                    movedLine = movedStart || movedEnd;

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

                    bool TryPlanHorizontalClampEndpointMove(
                        Point2d endpoint,
                        Point2d other,
                        double? forcedClampY,
                        out bool hasComponent,
                        out Point2d componentTarget,
                        out Point2d clampedTarget,
                        out bool moveEndpoint)
                    {
                        hasComponent = TryFindVerticalQsecComponentMidpoint(endpoint, out componentTarget);
                        clampedTarget = endpoint;
                        moveEndpoint = false;
                        if (!hasComponent && !forcedClampY.HasValue)
                        {
                            return false;
                        }

                        var clampY = forcedClampY ?? componentTarget.Y;
                        var clampX = hasComponent ? componentTarget.X : endpoint.X;
                        if (TryResolveHorizontalEndpointMidpointAxisX(
                            endpoint,
                            other,
                            clampY,
                            allowGenericQsecAxisFallback: true,
                            out var resolvedX))
                        {
                            clampX = resolvedX;
                        }

                        clampedTarget = new Point2d(clampX, clampY);
                        moveEndpoint = endpoint.GetDistanceTo(clampedTarget) > midpointEndpointMoveTol;
                        return true;
                    }

                    bool TryApplyQsecComponentClampMoves(
                        Entity writable,
                        bool moveStart,
                        Point2d targetStart,
                        bool moveEnd,
                        Point2d targetEnd)
                    {
                        var movedAny = false;
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

                        return movedAny;
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

                            movedAny = TryApplyQsecComponentClampMoves(
                                writable,
                                moveStart,
                                targetStart,
                                moveEnd,
                                targetEnd);
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
                                if (TryPlanHorizontalClampEndpointMove(
                                    p0,
                                    p1,
                                    forcedClampY,
                                    out var hasStartComponent,
                                    out var t0,
                                    out var clampedStart,
                                    out var shouldMoveStart))
                                {
                                    moveStart = shouldMoveStart;
                                    targetStart = clampedStart;

                                    if (traceClampThis && logger != null)
                                    {
                                        logger.WriteLine(
                                            $"TRACE-LSD-CLAMP start-cand hasComp={hasStartComponent} comp=({t0.X:0.###},{t0.Y:0.###}) cand=({clampedStart.X:0.###},{clampedStart.Y:0.###}) moveStart={moveStart}.");
                                    }
                                }
                            }

                            if (!p1OnHardForClamp)
                            {
                                if (TryPlanHorizontalClampEndpointMove(
                                    p1,
                                    p0,
                                    forcedClampY,
                                    out var hasEndComponent,
                                    out var t1,
                                    out var clampedEnd,
                                    out var shouldMoveEnd))
                                {
                                    moveEnd = shouldMoveEnd;
                                    targetEnd = clampedEnd;

                                    if (traceClampThis && logger != null)
                                    {
                                        logger.WriteLine(
                                            $"TRACE-LSD-CLAMP end-cand hasComp={hasEndComponent} comp=({t1.X:0.###},{t1.Y:0.###}) cand=({clampedEnd.X:0.###},{clampedEnd.Y:0.###}) moveEnd={moveEnd}.");
                                    }
                                }
                            }

                            movedAny = TryApplyQsecComponentClampMoves(
                                writable,
                                moveStart,
                                targetStart,
                                moveEnd,
                                targetEnd);

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

                    bool TryFindThirtyEscapeTarget(
                        Point2d endpoint,
                        Point2d other,
                        bool? preferZero,
                        out Point2d target)
                    {
                        return TryFindPreferredHardBoundaryMidpoint(
                                endpoint,
                                other,
                                preferZero,
                                out target,
                                maxMoveOverride: thirtyEscapeMaxMove) ||
                            TryFindPreferredHardBoundaryMidpointRelaxed(
                                endpoint,
                                other,
                                preferZero,
                                out target,
                                maxMoveOverride: thirtyEscapeMaxMove) ||
                            TryFindNearestHardBoundaryPoint(endpoint, other, preferZero, out target) ||
                            TryFindNearestHardBoundaryPoint(
                                endpoint,
                                other,
                                preferZero,
                                out target,
                                lateralTolOverride: thirtyEscapeLateralTol,
                                maxMoveOverride: thirtyEscapeMaxMove,
                                allowBacktrack: true) ||
                            TryFindNearestHardBoundaryPoint(
                                endpoint,
                                other,
                                preferZero: null,
                                out target,
                                lateralTolOverride: thirtyEscapeLateralTol,
                                maxMoveOverride: thirtyEscapeMaxMove,
                                allowBacktrack: true);
                    }

                    bool TryApplyThirtyEscapeMove(
                        Entity writable,
                        bool moveStart,
                        Point2d endpoint,
                        Point2d other,
                        bool? preferZero)
                    {
                        if (!TryFindThirtyEscapeTarget(endpoint, other, preferZero, out var target))
                        {
                            return false;
                        }

                        if (!TryMoveEndpoint(writable, moveStart, target, midpointEndpointMoveTol))
                        {
                            return false;
                        }

                        adjustedEndpoints++;
                        return true;
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
                                movedVertical = TryApplyThirtyEscapeMove(writable, moveStart: true, p0, p1, preferZero) || movedVertical;
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
                                movedVertical = TryApplyThirtyEscapeMove(writable, moveStart: false, p1, p0, preferZero) || movedVertical;
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
                            movedAny = TryApplyThirtyEscapeMove(writable, moveStart: true, p0, p1, preferZero) || movedAny;
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
                            movedAny = TryApplyThirtyEscapeMove(writable, moveStart: false, p1, p0, preferZero) || movedAny;
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

                bool IsEndpointOnThirtyOnly(Point2d endpoint) =>
                    IsEndpointOnBoundarySegmentsForEndpointEnforcement(endpoint, thirtyBoundarySegments, endpointTouchTol);

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

                void PlanBlindLineHardBoundaryExtension(
                    Point2d endpoint,
                    Point2d otherEndpoint,
                    bool moveStart,
                    ref EndpointMovePlan movePlan)
                {
                    scannedEndpoints++;
                    if (IsEndpointOnHardBoundary(endpoint))
                    {
                        alreadyOnHard++;
                        return;
                    }

                    if (IsPointOnAnyWindowBoundary(endpoint, outerBoundaryTol))
                    {
                        boundarySkipped++;
                        return;
                    }

                    if (IsEndpointOnThirtyOnly(endpoint))
                    {
                        onThirtyOnly++;
                    }

                    if (TryFindExtensionTarget(endpoint, otherEndpoint, out var target))
                    {
                        if (moveStart)
                        {
                            movePlan.MoveStart = true;
                            movePlan.TargetStart = target;
                        }
                        else
                        {
                            movePlan.MoveEnd = true;
                            movePlan.TargetEnd = target;
                        }

                        return;
                    }

                    noTarget++;
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

                    var movePlan = EndpointMovePlan.Create(p0, p1);

                    PlanBlindLineHardBoundaryExtension(p0, p1, moveStart: true, ref movePlan);
                    PlanBlindLineHardBoundaryExtension(p1, p0, moveStart: false, ref movePlan);

                    CancelShorterEndpointMoveForMinimumLength(ref movePlan, p0, p1, minRemainingLength);

                    if (!movePlan.HasAnyMoves)
                    {
                        continue;
                    }

                    var moveResult = ApplyEndpointMovePlanForEndpointEnforcement(writable, movePlan, endpointMoveTol);
                    adjustedEndpoints += moveResult.AdjustedEndpointCount;
                    if (moveResult.MovedAny)
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

        private static void CancelShorterEndpointMoveForMinimumLength(
            ref EndpointMovePlan movePlan,
            Point2d originalStart,
            Point2d originalEnd,
            double minRemainingLength)
        {
            if (!movePlan.MoveStart ||
                !movePlan.MoveEnd ||
                movePlan.TargetStart.GetDistanceTo(movePlan.TargetEnd) >= minRemainingLength)
            {
                return;
            }

            var startMoveDist = originalStart.GetDistanceTo(movePlan.TargetStart);
            var endMoveDist = originalEnd.GetDistanceTo(movePlan.TargetEnd);
            if (startMoveDist >= endMoveDist)
            {
                movePlan.MoveEnd = false;
            }
            else
            {
                movePlan.MoveStart = false;
            }
        }

        private static EndpointMoveApplyResult ApplyEndpointMovePlanForEndpointEnforcement(
            Entity writable,
            EndpointMovePlan movePlan,
            double moveTolerance)
        {
            var movedStart = movePlan.MoveStart &&
                             TryMoveEndpointForEndpointEnforcement(writable, moveStart: true, movePlan.TargetStart, moveTolerance);
            var movedEnd = movePlan.MoveEnd &&
                           TryMoveEndpointForEndpointEnforcement(writable, moveStart: false, movePlan.TargetEnd, moveTolerance);
            return new EndpointMoveApplyResult(movedStart, movedEnd);
        }

        private struct EndpointMovePlan
        {
            public static EndpointMovePlan Create(Point2d start, Point2d end)
            {
                return new EndpointMovePlan
                {
                    MoveStart = false,
                    MoveEnd = false,
                    TargetStart = start,
                    TargetEnd = end,
                };
            }

            public bool MoveStart { get; set; }

            public bool MoveEnd { get; set; }

            public Point2d TargetStart { get; set; }

            public Point2d TargetEnd { get; set; }

            public bool HasAnyMoves => MoveStart || MoveEnd;
        }

        private readonly struct EndpointMoveApplyResult
        {
            public EndpointMoveApplyResult(bool movedStart, bool movedEnd)
            {
                MovedStart = movedStart;
                MovedEnd = movedEnd;
            }

            public bool MovedStart { get; }

            public bool MovedEnd { get; }

            public bool MovedAny => MovedStart || MovedEnd;

            public int AdjustedEndpointCount => (MovedStart ? 1 : 0) + (MovedEnd ? 1 : 0);
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
                string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
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

        private static List<(Point2d A, Point2d B)> CollectEntitySegmentsForEndpointEnforcement(
            Entity ent,
            bool hasPrimarySegment,
            Point2d primaryA,
            Point2d primaryB)
        {
            var segments = new List<(Point2d A, Point2d B)>();

            void AddSegment(Point2d start, Point2d end)
            {
                if (start.GetDistanceTo(end) > 1e-4)
                {
                    segments.Add((start, end));
                }
            }

            if (ent is Line line)
            {
                AddSegment(
                    new Point2d(line.StartPoint.X, line.StartPoint.Y),
                    new Point2d(line.EndPoint.X, line.EndPoint.Y));
            }
            else if (ent is Polyline polyline && polyline.NumberOfVertices >= 2)
            {
                for (var vi = 0; vi < polyline.NumberOfVertices - 1; vi++)
                {
                    AddSegment(polyline.GetPoint2dAt(vi), polyline.GetPoint2dAt(vi + 1));
                }

                if (polyline.Closed)
                {
                    AddSegment(
                        polyline.GetPoint2dAt(polyline.NumberOfVertices - 1),
                        polyline.GetPoint2dAt(0));
                }
            }

            if (segments.Count == 0 && hasPrimarySegment)
            {
                segments.Add((primaryA, primaryB));
            }

            return segments;
        }

        private static List<(Point2d A, Point2d B)> CollectScopedEntitySegmentsForEndpointEnforcement(
            Entity ent,
            bool hasPrimarySegment,
            Point2d primaryA,
            Point2d primaryB,
            Func<Point2d, Point2d, bool> shouldKeepSegment,
            bool fallbackToAllSegments)
        {
            var entitySegments = CollectEntitySegmentsForEndpointEnforcement(ent, hasPrimarySegment, primaryA, primaryB);
            if (entitySegments.Count == 0)
            {
                return entitySegments;
            }

            var scopedSegments = new List<(Point2d A, Point2d B)>(entitySegments.Count);
            for (var si = 0; si < entitySegments.Count; si++)
            {
                var segment = entitySegments[si];
                if (shouldKeepSegment(segment.A, segment.B))
                {
                    scopedSegments.Add(segment);
                }
            }

            if (scopedSegments.Count == 0 && fallbackToAllSegments)
            {
                scopedSegments.AddRange(entitySegments);
            }

            return scopedSegments;
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

        private static bool TryIntersectInfiniteLineWithBoundedSegmentExtensionForEndpointEnforcement(
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





