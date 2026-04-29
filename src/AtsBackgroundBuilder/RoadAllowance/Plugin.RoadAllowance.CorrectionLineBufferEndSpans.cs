/////////////////////////////////////////////////////////////////////

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
        private static bool RestoreCorrectionLineBufferEndSpans(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);
            bool IsVerticalLike(Point2d a, Point2d b) => IsVerticalLikeForCorrectionLinePost(a, b);
            bool IsOnWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForPlugin(p, tol, clipWindows);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var segments = new List<CorrectionBufferEndSpanSegment>();
                var verticalAnchors = new List<CorrectionBufferEndSpanSegment>();

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

                    if (ent == null || ent.IsErased ||
                        !TryReadOpenLinearSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    if (!IsCorrectionBufferEndSpanLayer(layer) && !string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var segment = new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal, vertical);
                    segments.Add(segment);
                    if (vertical && IsCorrectionBufferVerticalAnchorLayer(layer))
                    {
                        verticalAnchors.Add(segment);
                    }
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var extendedCorrectionRows = 0;
                var createdCorrectionOuterCompanions = 0;
                var createdTwentyCompanions = 0;
                var normalizedOuterAxisZeroRows = 0;
                var retargetedVerticalCorrectionEndpoints = 0;
                var relayeredShortOrdinaryCorrectionSpans = 0;
                var normalizedOuterAxisZeroIds = new HashSet<ObjectId>();
                var samples = new List<string>();

                bool AreParallel(CorrectionBufferEndSpanSegment left, CorrectionBufferEndSpanSegment right, double minDot = 0.985)
                {
                    if (left.Length <= 1e-6 || right.Length <= 1e-6)
                    {
                        return false;
                    }

                    return Math.Abs(left.Unit.DotProduct(right.Unit)) >= minDot;
                }

                bool EndpointTouchesVerticalAnchor(
                    Point2d endpoint,
                    ObjectId sourceId,
                    double tolerance,
                    Func<string, bool>? layerFilter = null)
                {
                    for (var i = 0; i < verticalAnchors.Count; i++)
                    {
                        var anchor = verticalAnchors[i];
                        if (anchor.Id == sourceId)
                        {
                            continue;
                        }

                        if (layerFilter != null && !layerFilter(anchor.Layer))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, anchor.A, anchor.B) <= tolerance ||
                            endpoint.GetDistanceTo(anchor.A) <= tolerance ||
                            endpoint.GetDistanceTo(anchor.B) <= tolerance)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryProjectEndpointToWindowBoundary(
                    Point2d endpoint,
                    Point2d other,
                    out Point2d projected,
                    out double moveDistance)
                {
                    projected = endpoint;
                    moveDistance = 0.0;
                    var outward = endpoint - other;
                    var outwardLength = outward.Length;
                    if (outwardLength <= 1e-6)
                    {
                        return false;
                    }

                    var unit = outward / outwardLength;
                    var bestT = double.MaxValue;
                    Point2d bestPoint = default;
                    const double boundaryTol = 0.75;
                    const double minMove = 4.0;
                    const double maxMove = 130.0;

                    void TryCandidate(double t, double x, double y, Extents3d window)
                    {
                        if (t <= minMove || t > maxMove || t >= bestT)
                        {
                            return;
                        }

                        if (x < window.MinPoint.X - boundaryTol ||
                            x > window.MaxPoint.X + boundaryTol ||
                            y < window.MinPoint.Y - boundaryTol ||
                            y > window.MaxPoint.Y + boundaryTol)
                        {
                            return;
                        }

                        bestT = t;
                        bestPoint = new Point2d(x, y);
                    }

                    for (var wi = 0; wi < clipWindows.Count; wi++)
                    {
                        var window = clipWindows[wi];
                        if (endpoint.X < window.MinPoint.X - boundaryTol ||
                            endpoint.X > window.MaxPoint.X + boundaryTol ||
                            endpoint.Y < window.MinPoint.Y - boundaryTol ||
                            endpoint.Y > window.MaxPoint.Y + boundaryTol)
                        {
                            continue;
                        }

                        if (Math.Abs(unit.X) > 1e-9)
                        {
                            var tMinX = (window.MinPoint.X - endpoint.X) / unit.X;
                            TryCandidate(tMinX, window.MinPoint.X, endpoint.Y + (unit.Y * tMinX), window);
                            var tMaxX = (window.MaxPoint.X - endpoint.X) / unit.X;
                            TryCandidate(tMaxX, window.MaxPoint.X, endpoint.Y + (unit.Y * tMaxX), window);
                        }

                        if (Math.Abs(unit.Y) > 1e-9)
                        {
                            var tMinY = (window.MinPoint.Y - endpoint.Y) / unit.Y;
                            TryCandidate(tMinY, endpoint.X + (unit.X * tMinY), window.MinPoint.Y, window);
                            var tMaxY = (window.MaxPoint.Y - endpoint.Y) / unit.Y;
                            TryCandidate(tMaxY, endpoint.X + (unit.X * tMaxY), window.MaxPoint.Y, window);
                        }
                    }

                    if (bestT == double.MaxValue)
                    {
                        return false;
                    }

                    projected = bestPoint;
                    moveDistance = bestT;
                    return true;
                }

                bool HasNearbyCorrectionBoundaryWitness(CorrectionBufferEndSpanSegment source, Point2d projectedEndpoint)
                {
                    const double witnessDistance = 35.0;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (candidate.Id == source.Id ||
                            !candidate.Horizontal ||
                            !IsCorrectionBufferCorrectionLayer(candidate.Layer) ||
                            !AreParallel(source, candidate))
                        {
                            continue;
                        }

                        var candidateEndpointOnBoundary =
                            (IsOnWindowBoundary(candidate.A, 0.85) && candidate.A.GetDistanceTo(projectedEndpoint) <= witnessDistance) ||
                            (IsOnWindowBoundary(candidate.B, 0.85) && candidate.B.GetDistanceTo(projectedEndpoint) <= witnessDistance);
                        if (candidateEndpointOnBoundary)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasSameLayerForwardContinuation(CorrectionBufferEndSpanSegment source, Point2d endpoint, Point2d projectedEndpoint)
                {
                    var extension = projectedEndpoint - endpoint;
                    var extensionLength = extension.Length;
                    if (extensionLength <= 1.0)
                    {
                        return false;
                    }

                    var extensionUnit = extension / extensionLength;
                    const double continuationProjectionTol = 1.0;
                    const double continuationLineTol = 8.5;

                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (candidate.Id == source.Id ||
                            !candidate.Horizontal ||
                            !string.Equals(candidate.Layer, source.Layer, StringComparison.OrdinalIgnoreCase) ||
                            !AreParallel(source, candidate, minDot: 0.992))
                        {
                            continue;
                        }

                        var c0 = (candidate.A - endpoint).DotProduct(extensionUnit);
                        var c1 = (candidate.B - endpoint).DotProduct(extensionUnit);
                        var minProjection = Math.Min(c0, c1);
                        var maxProjection = Math.Max(c0, c1);
                        if (maxProjection < -continuationProjectionTol ||
                            minProjection > extensionLength + continuationProjectionTol)
                        {
                            continue;
                        }

                        var overlap = Math.Min(extensionLength, maxProjection) - Math.Max(0.0, minProjection);
                        if (overlap < 8.0)
                        {
                            continue;
                        }

                        if (DistancePointToInfiniteLine(candidate.Mid, endpoint, projectedEndpoint) <= continuationLineTol ||
                            DistancePointToSegment(endpoint + (extensionUnit * Math.Max(0.0, minProjection)), candidate.A, candidate.B) <= continuationLineTol ||
                            DistancePointToSegment(endpoint + (extensionUnit * Math.Min(extensionLength, maxProjection)), candidate.A, candidate.B) <= continuationLineTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                void UpdateSegment(int index, Point2d a, Point2d b, string? layer = null)
                {
                    var current = segments[index];
                    var updated = new CorrectionBufferEndSpanSegment(
                        current.Id,
                        layer ?? current.Layer,
                        a,
                        b,
                        IsHorizontalLike(a, b),
                        IsVerticalLike(a, b));
                    segments[index] = updated;

                    for (var i = 0; i < verticalAnchors.Count; i++)
                    {
                        if (verticalAnchors[i].Id == current.Id)
                        {
                            verticalAnchors[i] = updated;
                        }
                    }
                }

                bool TryRewriteSegmentEntity(Entity entity, Point2d a, Point2d b, string? layer = null)
                {
                    if (entity == null || entity.IsErased || a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(layer))
                    {
                        entity.Layer = layer;
                    }

                    entity.ColorIndex = 256;

                    if (entity is Line line)
                    {
                        line.StartPoint = new Point3d(a.X, a.Y, line.StartPoint.Z);
                        line.EndPoint = new Point3d(b.X, b.Y, line.EndPoint.Z);
                        return true;
                    }

                    if (entity is Polyline polyline && !polyline.Closed && polyline.NumberOfVertices >= 2)
                    {
                        polyline.SetPointAt(0, a);
                        polyline.SetPointAt(polyline.NumberOfVertices - 1, b);
                        return true;
                    }

                    return false;
                }

                bool TryRewriteSegment(int index, Point2d a, Point2d b, string? layer = null)
                {
                    if (index < 0 || index >= segments.Count)
                    {
                        return false;
                    }

                    var source = segments[index];
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return false;
                    }

                    if (writable == null ||
                        !TryRewriteSegmentEntity(writable, a, b, layer) ||
                        !TryReadOpenLinearSegment(writable, out var newA, out var newB))
                    {
                        return false;
                    }

                    UpdateSegment(index, newA, newB, layer ?? writable.Layer ?? source.Layer);
                    return true;
                }

                bool TryRelayerSegment(int index, string targetLayer)
                {
                    if (index < 0 || index >= segments.Count)
                    {
                        return false;
                    }

                    var source = segments[index];
                    if (string.Equals(source.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return false;
                    }

                    if (writable == null || writable.IsErased)
                    {
                        return false;
                    }

                    writable.Layer = targetLayer;
                    writable.ColorIndex = 256;
                    UpdateSegment(index, source.A, source.B, targetLayer);
                    return true;
                }

                for (var si = 0; si < segments.Count; si++)
                {
                    var source = segments[si];
                    if (!source.Horizontal || !IsCorrectionBufferCorrectionLayer(source.Layer))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? source.A : source.B;
                        var other = endpointIndex == 0 ? source.B : source.A;
                        if (IsOnWindowBoundary(endpoint, 0.85) ||
                            !EndpointTouchesVerticalAnchor(endpoint, source.Id, 1.35, IsCorrectionBufferCorrectionExtensionAnchorLayer) ||
                            !TryProjectEndpointToWindowBoundary(endpoint, other, out var target, out var moveDistance) ||
                            !HasNearbyCorrectionBoundaryWitness(source, target) ||
                            HasSameLayerForwardContinuation(source, endpoint, target))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) ||
                            writable.IsErased ||
                            !TryMoveEndpointForCorrectionLinePost(writable, endpointIndex == 0, target, 0.05) ||
                            !TryReadOpenLinearSegment(writable, out var newA, out var newB))
                        {
                            continue;
                        }

                        extendedCorrectionRows++;
                        UpdateSegment(si, newA, newB);
                        source = segments[si];
                        if (samples.Count < 12)
                        {
                            samples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "extend {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###} move={5:0.###}",
                                    source.Layer,
                                    endpoint.X,
                                    endpoint.Y,
                                    target.X,
                                    target.Y,
                                    moveDistance));
                        }
                    }
                }

                bool TryFindNearestCorrectionSide(
                    CorrectionBufferEndSpanSegment source,
                    out int sign)
                {
                    sign = 0;
                    var found = false;
                    var bestDistance = double.MaxValue;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (candidate.Id == source.Id ||
                            !candidate.Horizontal ||
                            !IsCorrectionBufferCorrectionLayer(candidate.Layer) ||
                            !AreParallel(source, candidate))
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(source.A, source.B, candidate.A, candidate.B);
                        var endpointGap = MinEndpointDistance(source.A, source.B, candidate.A, candidate.B);
                        if (overlap < 8.0 && endpointGap > 40.0)
                        {
                            continue;
                        }

                        var signedOffset = SignedDistanceToLine(candidate.Mid, source.A, source.B);
                        var absOffset = Math.Abs(signedOffset);
                        if (absOffset < CorrectionLinePostInsetMeters + 1.0 || absOffset > 35.0)
                        {
                            continue;
                        }

                        if (absOffset >= bestDistance)
                        {
                            continue;
                        }

                        bestDistance = absOffset;
                        sign = Math.Sign(signedOffset);
                        found = sign != 0;
                    }

                    return found;
                }

                bool HasMatchingLiveSegment(string layer, Point2d a, Point2d b, double endpointTolerance = 0.45)
                {
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (!string.Equals(candidate.Layer, layer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if ((candidate.A.GetDistanceTo(a) <= endpointTolerance && candidate.B.GetDistanceTo(b) <= endpointTolerance) ||
                            (candidate.A.GetDistanceTo(b) <= endpointTolerance && candidate.B.GetDistanceTo(a) <= endpointTolerance))
                        {
                            return true;
                        }

                        if (AreNearCollinearCovered(a, b, candidate.A, candidate.B, endpointTolerance))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasExistingCorrectionInsetCompanion(CorrectionBufferEndSpanSegment source)
                {
                    const double insetTolerance = 0.75;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (candidate.Id == source.Id ||
                            !candidate.Horizontal ||
                            !string.Equals(candidate.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                            !AreParallel(source, candidate, minDot: 0.992))
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(source.A, source.B, candidate.A, candidate.B);
                        if (overlap < Math.Min(12.0, source.Length * 0.40))
                        {
                            continue;
                        }

                        var absOffset = Math.Abs(SignedDistanceToLine(candidate.Mid, source.A, source.B));
                        if (Math.Abs(absOffset - CorrectionLinePostInsetMeters) <= insetTolerance)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasNearbyParallelSameLayerSegment(string layer, Point2d a, Point2d b, double lineTolerance, double minOverlap)
                {
                    var targetLength = a.GetDistanceTo(b);
                    if (targetLength <= 1e-6)
                    {
                        return false;
                    }

                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (!candidate.Horizontal ||
                            !string.Equals(candidate.Layer, layer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var candidateLength = candidate.Length;
                        if (candidateLength <= 1e-6 ||
                            Math.Abs(((b - a) / targetLength).DotProduct(candidate.Unit)) < 0.992)
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(a, b, candidate.A, candidate.B);
                        if (overlap < Math.Min(minOverlap, targetLength * 0.50))
                        {
                            continue;
                        }

                        if (DistancePointToInfiniteLine(candidate.Mid, a, b) <= lineTolerance ||
                            DistancePointToSegment(a, candidate.A, candidate.B) <= lineTolerance ||
                            DistancePointToSegment(b, candidate.A, candidate.B) <= lineTolerance)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                ObjectId AppendTwoPointPolyline(string layer, Point2d a, Point2d b)
                {
                    var polyline = new Polyline();
                    polyline.AddVertexAt(0, a, 0.0, 0.0, 0.0);
                    polyline.AddVertexAt(1, b, 0.0, 0.0, 0.0);
                    polyline.Layer = layer;
                    polyline.ColorIndex = 256;
                    var id = ms.AppendEntity(polyline);
                    tr.AddNewlyCreatedDBObject(polyline, true);
                    return id;
                }

                for (var si = 0; si < segments.Count; si++)
                {
                    var source = segments[si];
                    if (!source.Horizontal ||
                        !string.Equals(source.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                        HasExistingCorrectionInsetCompanion(source) ||
                        !TryFindNearestCorrectionSide(source, out var sideSign))
                    {
                        continue;
                    }

                    if (!IsOnWindowBoundary(source.A, 0.85) && !IsOnWindowBoundary(source.B, 0.85))
                    {
                        continue;
                    }

                    var offset = source.LeftNormal * (sideSign * CorrectionLinePostInsetMeters);
                    var newA = source.A + offset;
                    var newB = source.B + offset;
                    if (newA.GetDistanceTo(newB) <= 2.0 ||
                        !DoesSegmentIntersectAnyWindow(newA, newB) ||
                        HasMatchingLiveSegment(LayerUsecCorrection, newA, newB) ||
                        HasNearbyParallelSameLayerSegment(LayerUsecCorrection, newA, newB, lineTolerance: 5.75, minOverlap: 12.0))
                    {
                        continue;
                    }

                    var newId = AppendTwoPointPolyline(LayerUsecCorrection, newA, newB);
                    if (newId.IsNull)
                    {
                        continue;
                    }

                    var created = new CorrectionBufferEndSpanSegment(newId, LayerUsecCorrection, newA, newB, IsHorizontalLike(newA, newB), IsVerticalLike(newA, newB));
                    segments.Add(created);
                    createdCorrectionOuterCompanions++;
                    if (samples.Count < 12)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "create {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###}",
                                LayerUsecCorrection,
                                newA.X,
                                newA.Y,
                                newB.X,
                                newB.Y));
                    }
                }

                bool TryFindTwentyEndpointNear(
                    Point2d thirtyEndpoint,
                    out Point2d twentyEndpoint)
                {
                    twentyEndpoint = default;
                    var bestEndpoint = default(Point2d);
                    var found = false;
                    var bestDistance = double.MaxValue;
                    const double minDistance = 4.0;
                    const double maxDistance = 18.0;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var candidate = segments[i];
                        if (!candidate.Vertical || !IsCorrectionBufferTwentyLayer(candidate.Layer))
                        {
                            continue;
                        }

                        void TryEndpoint(Point2d endpoint)
                        {
                            var distance = endpoint.GetDistanceTo(thirtyEndpoint);
                            if (distance < minDistance || distance > maxDistance || distance >= bestDistance)
                            {
                                return;
                            }

                            bestDistance = distance;
                            bestEndpoint = endpoint;
                            found = true;
                        }

                        TryEndpoint(candidate.A);
                        TryEndpoint(candidate.B);
                    }

                    if (found)
                    {
                        twentyEndpoint = bestEndpoint;
                    }

                    return found;
                }

                for (var si = 0; si < segments.Count; si++)
                {
                    var source = segments[si];
                    if (!source.Horizontal ||
                        !IsCorrectionBufferThirtyLayer(source.Layer) ||
                        source.Length > 160.0)
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var thirtyEndpoint = endpointIndex == 0 ? source.A : source.B;
                        var terminal = endpointIndex == 0 ? source.B : source.A;
                        if (!TryFindTwentyEndpointNear(thirtyEndpoint, out var twentyStart))
                        {
                            continue;
                        }

                        var direction = terminal - thirtyEndpoint;
                        var length = direction.Length;
                        if (length <= 1e-6)
                        {
                            continue;
                        }

                        var unit = direction / length;
                        double t;
                        if (Math.Abs(unit.X) >= Math.Abs(unit.Y))
                        {
                            if (Math.Abs(unit.X) <= 1e-9)
                            {
                                continue;
                            }

                            t = (terminal.X - twentyStart.X) / unit.X;
                        }
                        else
                        {
                            if (Math.Abs(unit.Y) <= 1e-9)
                            {
                                continue;
                            }

                            t = (terminal.Y - twentyStart.Y) / unit.Y;
                        }

                        if (t <= 5.0 || t > length + 35.0)
                        {
                            continue;
                        }

                        var twentyEnd = twentyStart + (unit * t);
                        if (!DoesSegmentIntersectAnyWindow(twentyStart, twentyEnd) ||
                            HasMatchingLiveSegment(LayerUsecTwenty, twentyStart, twentyEnd, endpointTolerance: 0.75))
                        {
                            continue;
                        }

                        var newId = AppendTwoPointPolyline(LayerUsecTwenty, twentyStart, twentyEnd);
                        if (newId.IsNull)
                        {
                            continue;
                        }

                        var created = new CorrectionBufferEndSpanSegment(newId, LayerUsecTwenty, twentyStart, twentyEnd, IsHorizontalLike(twentyStart, twentyEnd), IsVerticalLike(twentyStart, twentyEnd));
                        segments.Add(created);
                        createdTwentyCompanions++;
                        if (samples.Count < 12)
                        {
                            samples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "create {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###}",
                                    LayerUsecTwenty,
                                    twentyStart.X,
                                    twentyStart.Y,
                                    twentyEnd.X,
                                    twentyEnd.Y));
                        }
                    }
                }

                bool TryFindSameAxisCorrectionOuter(
                    CorrectionBufferEndSpanSegment source,
                    out CorrectionBufferEndSpanSegment outer)
                {
                    outer = default;
                    if (!source.Horizontal ||
                        !string.Equals(source.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var found = false;
                    var bestScore = double.MaxValue;
                    const double sameAxisLineTol = 1.25;
                    for (var ci = 0; ci < segments.Count; ci++)
                    {
                        var candidate = segments[ci];
                        if (candidate.Id == source.Id ||
                            !candidate.Horizontal ||
                            !string.Equals(candidate.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                            !AreParallel(source, candidate, minDot: 0.992))
                        {
                            continue;
                        }

                        var sourceLineOffset = Math.Max(
                            DistancePointToInfiniteLine(source.A, candidate.A, candidate.B),
                            DistancePointToInfiniteLine(source.B, candidate.A, candidate.B));
                        if (sourceLineOffset > sameAxisLineTol)
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(source.A, source.B, candidate.A, candidate.B);
                        var endpointGap = MinEndpointDistance(source.A, source.B, candidate.A, candidate.B);
                        if (overlap < Math.Min(source.Length, candidate.Length) * 0.55 &&
                            endpointGap > 35.0)
                        {
                            continue;
                        }

                        var score = sourceLineOffset + Math.Max(0.0, 20.0 - overlap) * 0.01 + endpointGap * 0.001;
                        if (!found || score < bestScore)
                        {
                            found = true;
                            bestScore = score;
                            outer = candidate;
                        }
                    }

                    return found;
                }

                double DistanceToExistingCorrectionZero(Point2d point, ObjectId sourceId)
                {
                    var best = double.MaxValue;
                    for (var ci = 0; ci < segments.Count; ci++)
                    {
                        var candidate = segments[ci];
                        if (candidate.Id == sourceId ||
                            !candidate.Horizontal ||
                            !string.Equals(candidate.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        best = Math.Min(best, DistancePointToSegment(point, candidate.A, candidate.B));
                        best = Math.Min(best, point.GetDistanceTo(candidate.A));
                        best = Math.Min(best, point.GetDistanceTo(candidate.B));
                    }

                    return best;
                }

                bool TryChooseExistingZeroOffsetSide(
                    CorrectionBufferEndSpanSegment source,
                    out Point2d offsetA,
                    out Point2d offsetB,
                    out double score)
                {
                    offsetA = source.A;
                    offsetB = source.B;
                    score = double.MaxValue;
                    if (source.Length <= 1e-6)
                    {
                        return false;
                    }

                    var found = false;
                    var offsets = new[]
                    {
                        source.LeftNormal * CorrectionLinePostInsetMeters,
                        source.LeftNormal * -CorrectionLinePostInsetMeters,
                    };

                    for (var oi = 0; oi < offsets.Length; oi++)
                    {
                        var candidateA = source.A + offsets[oi];
                        var candidateB = source.B + offsets[oi];
                        var distanceA = DistanceToExistingCorrectionZero(candidateA, source.Id);
                        var distanceB = DistanceToExistingCorrectionZero(candidateB, source.Id);
                        var bestEndpointDistance = Math.Min(distanceA, distanceB);
                        if (bestEndpointDistance > 1.75)
                        {
                            continue;
                        }

                        var candidateScore = bestEndpointDistance + Math.Abs(distanceA - distanceB) * 0.001;
                        if (!found || candidateScore < score)
                        {
                            found = true;
                            score = candidateScore;
                            offsetA = candidateA;
                            offsetB = candidateB;
                        }
                    }

                    return found;
                }

                int NormalizeCorrectionZeroRowsOnOuterAxis()
                {
                    var normalized = 0;
                    for (var si = 0; si < segments.Count; si++)
                    {
                        var source = segments[si];
                        if (!source.Horizontal ||
                            !string.Equals(source.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                            !TryFindSameAxisCorrectionOuter(source, out _) ||
                            !TryChooseExistingZeroOffsetSide(source, out var offsetA, out var offsetB, out var offsetScore))
                        {
                            continue;
                        }

                        if (HasMatchingLiveSegment(LayerUsecCorrectionZero, offsetA, offsetB, endpointTolerance: 0.35))
                        {
                            continue;
                        }

                        if (!TryRewriteSegment(si, offsetA, offsetB, LayerUsecCorrectionZero))
                        {
                            continue;
                        }

                        normalizedOuterAxisZeroIds.Add(source.Id);
                        normalized++;
                        if (samples.Count < 12)
                        {
                            samples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "offset {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###} score={5:0.###}",
                                    LayerUsecCorrectionZero,
                                    offsetA.X,
                                    offsetA.Y,
                                    offsetB.X,
                                    offsetB.Y,
                                    offsetScore));
                        }
                    }

                    return normalized;
                }

                bool EndpointTouchesCorrectionRow(Point2d endpoint, ObjectId sourceId, double tolerance)
                {
                    for (var ci = 0; ci < segments.Count; ci++)
                    {
                        var candidate = segments[ci];
                        if (candidate.Id == sourceId ||
                            !candidate.Horizontal ||
                            !IsCorrectionBufferCorrectionLayer(candidate.Layer))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, candidate.A, candidate.B) <= tolerance ||
                            endpoint.GetDistanceTo(candidate.A) <= tolerance ||
                            endpoint.GetDistanceTo(candidate.B) <= tolerance)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindVerticalCorrectionEndpointTarget(
                    CorrectionBufferEndSpanSegment source,
                    int endpointIndex,
                    out Point2d target,
                    out string targetLayer,
                    out double moveDistance)
                {
                    target = endpointIndex == 0 ? source.A : source.B;
                    targetLayer = string.Empty;
                    moveDistance = double.MaxValue;
                    if (!source.Vertical ||
                        !(IsCorrectionBufferZeroLayer(source.Layer) ||
                          IsCorrectionBufferTwentyLayer(source.Layer) ||
                          IsCorrectionBufferThirtyLayer(source.Layer)))
                    {
                        return false;
                    }

                    var endpoint = endpointIndex == 0 ? source.A : source.B;
                    var other = endpointIndex == 0 ? source.B : source.A;
                    var axis = endpoint - other;
                    var length = axis.Length;
                    if (length <= 1e-6)
                    {
                        return false;
                    }

                    var unit = axis / length;
                    var otherTouchesCorrection = EndpointTouchesCorrectionRow(other, source.Id, 1.35);
                    var found = false;
                    var bestScore = double.MaxValue;

                    for (var ci = 0; ci < segments.Count; ci++)
                    {
                        var row = segments[ci];
                        if (row.Id == source.Id ||
                            !row.Horizontal ||
                            !IsCorrectionBufferCorrectionLayer(row.Layer) ||
                            !TryIntersectInfiniteLinesForPluginGeometry(source.A, source.B, row.A, row.B, out var intersection) ||
                            DistancePointToSegment(intersection, row.A, row.B) > 1.25)
                        {
                            continue;
                        }

                        var along = (intersection - other).DotProduct(unit);
                        var move = endpoint.GetDistanceTo(intersection);
                        if (along < -0.50)
                        {
                            continue;
                        }

                        var isLongOverrunTrim =
                            string.Equals(source.Layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(row.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                            row.Length <= 180.0 &&
                            length > 160.0 &&
                            otherTouchesCorrection &&
                            along >= 4.0 &&
                            along <= 45.0 &&
                            length - along >= 80.0;
                        var isShortExtensionToCorrection =
                            normalizedOuterAxisZeroIds.Contains(row.Id) &&
                            along > length + 1.50 &&
                            along <= length + CorrectionLinePostInsetMeters + 1.50 &&
                            move <= CorrectionLinePostInsetMeters + 1.50;

                        if (!isLongOverrunTrim && !isShortExtensionToCorrection)
                        {
                            continue;
                        }

                        var score = isLongOverrunTrim
                            ? along
                            : 100.0 + move;
                        if (!found || score < bestScore)
                        {
                            found = true;
                            bestScore = score;
                            target = intersection;
                            targetLayer = row.Layer;
                            moveDistance = move;
                        }
                    }

                    return found;
                }

                int RetargetVerticalOrdinaryEndpointsAtCorrectionRows()
                {
                    var moved = 0;
                    for (var si = 0; si < segments.Count; si++)
                    {
                        var source = segments[si];
                        if (!source.Vertical)
                        {
                            continue;
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            source = segments[si];
                            if (!TryFindVerticalCorrectionEndpointTarget(
                                    source,
                                    endpointIndex,
                                    out var target,
                                    out var targetLayer,
                                    out var moveDistance))
                            {
                                continue;
                            }

                            var current = endpointIndex == 0 ? source.A : source.B;
                            if (current.GetDistanceTo(target) <= 0.05)
                            {
                                continue;
                            }

                            if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) ||
                                writable.IsErased ||
                                !TryMoveEndpointForCorrectionLinePost(writable, endpointIndex == 0, target, 0.05) ||
                                !TryReadOpenLinearSegment(writable, out var newA, out var newB))
                            {
                                continue;
                            }

                            moved++;
                            UpdateSegment(si, newA, newB);
                            if (samples.Count < 12)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "vertical {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###} targetLayer={5} move={6:0.###}",
                                        source.Layer,
                                        current.X,
                                        current.Y,
                                        target.X,
                                        target.Y,
                                        targetLayer,
                                        moveDistance));
                            }
                        }
                    }

                    return moved;
                }

                bool TryFindCorrectionProjectionForShortOrdinary(
                    CorrectionBufferEndSpanSegment source,
                    string targetLayer)
                {
                    const double maxLineDistance = 3.0;
                    const double minEndpointGap = 8.0;
                    const double maxEndpointGap = 42.0;
                    for (var ci = 0; ci < segments.Count; ci++)
                    {
                        var row = segments[ci];
                        if (row.Id == source.Id ||
                            !row.Horizontal ||
                            !string.Equals(row.Layer, targetLayer, StringComparison.OrdinalIgnoreCase) ||
                            !AreParallel(source, row, minDot: 0.992))
                        {
                            continue;
                        }

                        var maxDistanceToProjection = Math.Max(
                            DistancePointToInfiniteLine(source.A, row.A, row.B),
                            DistancePointToInfiniteLine(source.B, row.A, row.B));
                        if (maxDistanceToProjection > maxLineDistance)
                        {
                            continue;
                        }

                        var endpointGap = MinEndpointDistance(source.A, source.B, row.A, row.B);
                        if (endpointGap < minEndpointGap || endpointGap > maxEndpointGap)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                int RelayerShortOrdinaryCorrectionProjectionSpans()
                {
                    var relayered = 0;
                    for (var si = 0; si < segments.Count; si++)
                    {
                        var source = segments[si];
                        if (!source.Horizontal ||
                            source.Length > 160.0 ||
                            (!IsOnWindowBoundary(source.A, 1.75) && !IsOnWindowBoundary(source.B, 1.75)) ||
                            (!EndpointTouchesVerticalAnchor(source.A, source.Id, 1.75) &&
                             !EndpointTouchesVerticalAnchor(source.B, source.Id, 1.75)))
                        {
                            continue;
                        }

                        string targetLayer;
                        if (IsCorrectionBufferTwentyLayer(source.Layer))
                        {
                            targetLayer = LayerUsecCorrectionZero;
                        }
                        else if (IsCorrectionBufferThirtyLayer(source.Layer))
                        {
                            targetLayer = LayerUsecCorrection;
                        }
                        else
                        {
                            continue;
                        }

                        if (!TryFindCorrectionProjectionForShortOrdinary(source, targetLayer) ||
                            HasMatchingLiveSegment(targetLayer, source.A, source.B, endpointTolerance: 0.35) ||
                            !TryRelayerSegment(si, targetLayer))
                        {
                            continue;
                        }

                        relayered++;
                        if (samples.Count < 12)
                        {
                            samples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "relayer {0}->{1} {2:0.###},{3:0.###}->{4:0.###},{5:0.###}",
                                    source.Layer,
                                    targetLayer,
                                    source.A.X,
                                    source.A.Y,
                                    source.B.X,
                                    source.B.Y));
                        }
                    }

                    return relayered;
                }

                normalizedOuterAxisZeroRows += NormalizeCorrectionZeroRowsOnOuterAxis();
                retargetedVerticalCorrectionEndpoints += RetargetVerticalOrdinaryEndpointsAtCorrectionRows();
                relayeredShortOrdinaryCorrectionSpans += RelayerShortOrdinaryCorrectionProjectionSpans();

                tr.Commit();
                var changed = extendedCorrectionRows > 0 ||
                              createdCorrectionOuterCompanions > 0 ||
                              createdTwentyCompanions > 0 ||
                              normalizedOuterAxisZeroRows > 0 ||
                              retargetedVerticalCorrectionEndpoints > 0 ||
                              relayeredShortOrdinaryCorrectionSpans > 0;
                if (changed)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: restored 100m buffer end spans extendedCorrection={extendedCorrectionRows}, createdCorrectionOuter={createdCorrectionOuterCompanions}, createdTwenty={createdTwentyCompanions}, normalizedOuterAxisZero={normalizedOuterAxisZeroRows}, retargetedVertical={retargetedVerticalCorrectionEndpoints}, relayeredShortOrdinary={relayeredShortOrdinaryCorrectionSpans}.");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   buffer-end " + samples[i]);
                    }
                }

                return changed;
            }
        }

        private static bool RetargetVerticalQsecEndpointsToCorrectionBufferEndRows(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);
            bool IsVerticalLike(Point2d a, Point2d b) => IsVerticalLikeForCorrectionLinePost(a, b);
            bool IsOnWindowBoundary(Point2d p, double tol) => IsPointOnAnyWindowBoundaryForPlugin(p, tol, clipWindows);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var correctionRows = new List<CorrectionBufferEndSpanSegment>();
                var qsecRows = new List<CorrectionBufferEndSpanSegment>();

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

                    if (ent == null || ent.IsErased ||
                        !TryReadOpenLinearSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsCorrectionBufferCorrectionLayer(layer) && IsHorizontalLike(a, b))
                    {
                        correctionRows.Add(new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal: true, vertical: false));
                        continue;
                    }

                    if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase) && IsVerticalLike(a, b))
                    {
                        qsecRows.Add(new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal: false, vertical: true));
                    }
                }

                if (correctionRows.Count == 0 || qsecRows.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                double SegmentParameter(Point2d point, Point2d a, Point2d b)
                {
                    var ab = b - a;
                    var len2 = ab.DotProduct(ab);
                    if (len2 <= 1e-9)
                    {
                        return 0.0;
                    }

                    return (point - a).DotProduct(ab) / len2;
                }

                bool IsNearCorrectionRowBufferEndpoint(CorrectionBufferEndSpanSegment row, Point2d target)
                {
                    return (IsOnWindowBoundary(row.A, 1.25) && target.GetDistanceTo(row.A) <= 2.0) ||
                           (IsOnWindowBoundary(row.B, 1.25) && target.GetDistanceTo(row.B) <= 2.0) ||
                           IsOnWindowBoundary(target, 1.75);
                }

                var retargeted = 0;
                var samples = new List<string>();
                const double minMove = 10.0;
                const double maxMove = 45.0;
                const double rowTouchTol = 0.95;

                for (var qi = 0; qi < qsecRows.Count; qi++)
                {
                    var qsec = qsecRows[qi];
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(qsec.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        !TryReadOpenLinearSegment(writable, out var currentA, out var currentB) ||
                        !IsVerticalLike(currentA, currentB))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? currentA : currentB;
                        var other = endpointIndex == 0 ? currentB : currentA;
                        var endpointDirection = endpoint - other;
                        if (endpointDirection.Length <= 1e-6)
                        {
                            continue;
                        }

                        var bestFound = false;
                        var bestTarget = endpoint;
                        var bestMove = double.MaxValue;
                        var bestLayer = string.Empty;

                        for (var ri = 0; ri < correctionRows.Count; ri++)
                        {
                            var row = correctionRows[ri];
                            if (!TryIntersectInfiniteLinesForPluginGeometry(endpoint, other, row.A, row.B, out var intersection))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(intersection, row.A, row.B) > rowTouchTol ||
                                !IsNearCorrectionRowBufferEndpoint(row, intersection))
                            {
                                continue;
                            }

                            var parameter = SegmentParameter(intersection, row.A, row.B);
                            if (parameter < -0.01 || parameter > 1.01)
                            {
                                continue;
                            }

                            var moveVector = intersection - endpoint;
                            var move = moveVector.Length;
                            if (move < minMove || move > maxMove ||
                                moveVector.DotProduct(endpointDirection) <= 0.0)
                            {
                                continue;
                            }

                            if (move >= bestMove)
                            {
                                continue;
                            }

                            bestFound = true;
                            bestMove = move;
                            bestTarget = intersection;
                            bestLayer = row.Layer;
                        }

                        if (!bestFound)
                        {
                            continue;
                        }

                        if (TryMoveEndpointForCorrectionLinePost(writable, endpointIndex == 0, bestTarget, 0.05) &&
                            TryReadOpenLinearSegment(writable, out currentA, out currentB))
                        {
                            retargeted++;
                            if (samples.Count < 8)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "{0} from={1:0.###},{2:0.###} to={3:0.###},{4:0.###} targetLayer={5} move={6:0.###}",
                                        qsec.Id.Handle.ToString(),
                                        endpoint.X,
                                        endpoint.Y,
                                        bestTarget.X,
                                        bestTarget.Y,
                                        bestLayer,
                                        bestMove));
                            }
                        }
                    }
                }

                tr.Commit();
                if (retargeted > 0)
                {
                    logger?.WriteLine($"CorrectionLine: retargeted vertical QSEC endpoints to 100m correction buffer rows moved={retargeted}.");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   qsec-buffer " + samples[i]);
                    }
                }

                return retargeted > 0;
            }
        }

        private static bool NormalizeSouthCorrectionQsecEndpoints(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);
            bool IsVerticalLike(Point2d a, Point2d b) => IsVerticalLikeForCorrectionLinePost(a, b);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var correctionOuterRows = new List<CorrectionBufferEndSpanSegment>();
                var correctionZeroRows = new List<CorrectionBufferEndSpanSegment>();
                var hardBoundaryRows = new List<CorrectionBufferEndSpanSegment>();
                var qsecRows = new List<CorrectionBufferEndSpanSegment>();

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

                    if (ent == null || ent.IsErased ||
                        !TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (horizontal &&
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        var row = new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal: true, vertical: false);
                        correctionOuterRows.Add(row);
                        hardBoundaryRows.Add(row);
                        continue;
                    }

                    if (horizontal &&
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        var row = new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal: true, vertical: false);
                        correctionZeroRows.Add(row);
                        hardBoundaryRows.Add(row);
                        continue;
                    }

                    if ((horizontal || vertical) &&
                        (IsUsecLayer(layer) ||
                         string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase)))
                    {
                        hardBoundaryRows.Add(new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal, vertical));
                        continue;
                    }

                    if (vertical &&
                        string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        qsecRows.Add(new CorrectionBufferEndSpanSegment(id, layer, a, b, horizontal: false, vertical: true));
                    }
                }

                if (qsecRows.Count == 0 || correctionOuterRows.Count == 0 || correctionZeroRows.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                double SegmentDistanceToRows(Point2d a, Point2d b, IReadOnlyList<CorrectionBufferEndSpanSegment> rows)
                {
                    var best = double.MaxValue;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        best = Math.Min(best, DistancePointToSegment(a, row.A, row.B));
                        best = Math.Min(best, DistancePointToSegment(b, row.A, row.B));
                        best = Math.Min(best, DistancePointToSegment(row.A, a, b));
                        best = Math.Min(best, DistancePointToSegment(row.B, a, b));
                    }

                    return best;
                }

                bool EndpointTouchesHardBoundary(Point2d endpoint, ObjectId selfId)
                {
                    for (var i = 0; i < hardBoundaryRows.Count; i++)
                    {
                        var row = hardBoundaryRows[i];
                        if (row.Id == selfId)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, row.A, row.B) <= 1.25 ||
                            endpoint.GetDistanceTo(row.A) <= 1.25 ||
                            endpoint.GetDistanceTo(row.B) <= 1.25)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindCorrectionZeroOffsetTarget(
                    Point2d endpoint,
                    Point2d other,
                    out Point2d target)
                {
                    target = endpoint;
                    var endpointDirection = endpoint - other;
                    if (endpointDirection.Length <= 1e-6)
                    {
                        return false;
                    }

                    var bestMove = double.MaxValue;
                    var bestTarget = endpoint;

                    for (var oi = 0; oi < correctionOuterRows.Count; oi++)
                    {
                        var outer = correctionOuterRows[oi];
                        if (DistancePointToSegment(endpoint, outer.A, outer.B) > 0.90)
                        {
                            continue;
                        }

                        for (var zi = 0; zi < correctionZeroRows.Count; zi++)
                        {
                            var zero = correctionZeroRows[zi];
                            if (Math.Abs(outer.Unit.DotProduct(zero.Unit)) < 0.992)
                            {
                                continue;
                            }

                            var overlap = GetProjectedOverlap(outer.A, outer.B, zero.A, zero.B);
                            var endpointGap = MinEndpointDistance(outer.A, outer.B, zero.A, zero.B);
                            if (overlap < 8.0 && endpointGap > CorrectionLinePostInsetMeters + 2.0)
                            {
                                continue;
                            }

                            var offset = Math.Abs(SignedDistanceToLine(zero.Mid, outer.A, outer.B));
                            if (Math.Abs(offset - CorrectionLinePostInsetMeters) > 0.75)
                            {
                                continue;
                            }

                            if (!TryIntersectInfiniteLinesForPluginGeometry(endpoint, other, zero.A, zero.B, out var intersection) ||
                                DistancePointToSegment(intersection, zero.A, zero.B) > 1.10)
                            {
                                continue;
                            }

                            var moveVector = intersection - endpoint;
                            var move = moveVector.Length;
                            if (move < 2.50 ||
                                move > CorrectionLinePostInsetMeters + 1.50 ||
                                moveVector.DotProduct(endpointDirection) <= 0.0)
                            {
                                continue;
                            }

                            if (move >= bestMove)
                            {
                                continue;
                            }

                            bestMove = move;
                            bestTarget = intersection;
                        }
                    }

                    if (bestMove == double.MaxValue)
                    {
                        return false;
                    }

                    target = bestTarget;
                    return true;
                }

                var correctionRows = correctionOuterRows.Concat(correctionZeroRows).ToList();
                var detachedFloatingQsecIds = new HashSet<ObjectId>();
                for (var qi = 0; qi < qsecRows.Count; qi++)
                {
                    var qsec = qsecRows[qi];
                    if (!DoesSegmentIntersectAnyWindow(qsec.A, qsec.B) ||
                        qsec.Length > 180.0)
                    {
                        continue;
                    }

                    var touchesBoundary =
                        EndpointTouchesHardBoundary(qsec.A, qsec.Id) ||
                        EndpointTouchesHardBoundary(qsec.B, qsec.Id);
                    if (touchesBoundary)
                    {
                        continue;
                    }

                    if (SegmentDistanceToRows(qsec.A, qsec.B, correctionRows) <= 65.0)
                    {
                        detachedFloatingQsecIds.Add(qsec.Id);
                    }
                }

                if (detachedFloatingQsecIds.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var moved = 0;
                var erased = 0;
                var samples = new List<string>();

                for (var qi = 0; qi < qsecRows.Count; qi++)
                {
                    var qsec = qsecRows[qi];
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(qsec.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        !TryReadOpenLinearSegment(writable, out var currentA, out var currentB) ||
                        !IsVerticalLike(currentA, currentB))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(currentA, currentB))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? currentA : currentB;
                        var other = endpointIndex == 0 ? currentB : currentA;
                        if (!TryFindCorrectionZeroOffsetTarget(endpoint, other, out var target))
                        {
                            continue;
                        }

                        if (TryMoveEndpointForCorrectionLinePost(writable, endpointIndex == 0, target, 0.05) &&
                            TryReadOpenLinearSegment(writable, out currentA, out currentB))
                        {
                            moved++;
                            if (samples.Count < 10)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "move {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###}",
                                        qsec.Id.Handle.ToString(),
                                        endpoint.X,
                                        endpoint.Y,
                                        target.X,
                                        target.Y));
                            }
                        }
                    }

                    if (writable.IsErased ||
                        !TryReadOpenLinearSegment(writable, out currentA, out currentB) ||
                        !IsVerticalLike(currentA, currentB))
                    {
                        continue;
                    }

                    var length = currentA.GetDistanceTo(currentB);
                    if (length > 180.0)
                    {
                        continue;
                    }

                    var touchesBoundary =
                        EndpointTouchesHardBoundary(currentA, qsec.Id) ||
                        EndpointTouchesHardBoundary(currentB, qsec.Id);
                    var nearCorrection = SegmentDistanceToRows(
                        currentA,
                        currentB,
                        correctionRows) <= 65.0;
                    if (touchesBoundary || !nearCorrection)
                    {
                        continue;
                    }

                    writable.Erase();
                    erased++;
                    if (samples.Count < 10)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "erase {0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###}",
                                qsec.Id.Handle.ToString(),
                                currentA.X,
                                currentA.Y,
                                currentB.X,
                                currentB.Y));
                    }
                }

                tr.Commit();
                if (moved > 0 || erased > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized south correction QSEC endpoints moved={moved}, erasedFloating={erased}.");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   south-correction-qsec " + samples[i]);
                    }
                }

                return moved > 0 || erased > 0;
            }
        }

        private static bool IsCorrectionBufferEndSpanLayer(string layer)
        {
            return IsCorrectionBufferCorrectionLayer(layer) ||
                   IsCorrectionBufferZeroLayer(layer) ||
                   IsCorrectionBufferTwentyLayer(layer) ||
                   IsCorrectionBufferThirtyLayer(layer) ||
                   string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionBufferCorrectionLayer(string layer)
        {
            return string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionBufferZeroLayer(string layer)
        {
            return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionBufferTwentyLayer(string layer)
        {
            return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionBufferThirtyLayer(string layer)
        {
            return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionBufferVerticalAnchorLayer(string layer)
        {
            return IsCorrectionBufferZeroLayer(layer) ||
                   IsCorrectionBufferTwentyLayer(layer) ||
                   IsCorrectionBufferThirtyLayer(layer) ||
                   IsCorrectionBufferCorrectionLayer(layer) ||
                   string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionBufferCorrectionExtensionAnchorLayer(string layer)
        {
            return IsCorrectionBufferZeroLayer(layer) ||
                   IsCorrectionBufferCorrectionLayer(layer) ||
                   string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase);
        }

        private static double SignedDistanceToLine(Point2d point, Point2d a, Point2d b)
        {
            var ab = b - a;
            var length = ab.Length;
            if (length <= 1e-9)
            {
                return 0.0;
            }

            return ((ab.X * (point.Y - a.Y)) - (ab.Y * (point.X - a.X))) / length;
        }

        private static double GetProjectedOverlap(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
        {
            var axisVector = a1 - a0;
            var axisLength = axisVector.Length;
            if (axisLength <= 1e-9)
            {
                return 0.0;
            }

            var axis = axisVector / axisLength;
            var bS0 = (b0 - a0).DotProduct(axis);
            var bS1 = (b1 - a0).DotProduct(axis);
            return Math.Min(axisLength, Math.Max(bS0, bS1)) - Math.Max(0.0, Math.Min(bS0, bS1));
        }

        private static double MinEndpointDistance(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
        {
            return Math.Min(
                Math.Min(a0.GetDistanceTo(b0), a0.GetDistanceTo(b1)),
                Math.Min(a1.GetDistanceTo(b0), a1.GetDistanceTo(b1)));
        }

        private static bool AreNearCollinearCovered(Point2d targetA, Point2d targetB, Point2d candidateA, Point2d candidateB, double tolerance)
        {
            var targetVector = targetB - targetA;
            var candidateVector = candidateB - candidateA;
            var targetLength = targetVector.Length;
            var candidateLength = candidateVector.Length;
            if (targetLength <= 1e-9 || candidateLength <= 1e-9)
            {
                return false;
            }

            var targetUnit = targetVector / targetLength;
            var candidateUnit = candidateVector / candidateLength;
            if (Math.Abs(targetUnit.DotProduct(candidateUnit)) < 0.995)
            {
                return false;
            }

            if (DistancePointToInfiniteLine(candidateA, targetA, targetB) > tolerance ||
                DistancePointToInfiniteLine(candidateB, targetA, targetB) > tolerance)
            {
                return false;
            }

            var overlap = GetProjectedOverlap(targetA, targetB, candidateA, candidateB);
            return overlap >= Math.Min(targetLength, candidateLength) - 0.75;
        }

        private readonly struct CorrectionBufferEndSpanSegment
        {
            public CorrectionBufferEndSpanSegment(
                ObjectId id,
                string layer,
                Point2d a,
                Point2d b,
                bool horizontal,
                bool vertical)
            {
                Id = id;
                Layer = layer ?? string.Empty;
                A = a;
                B = b;
                Horizontal = horizontal;
                Vertical = vertical;
                var direction = b - a;
                Length = direction.Length;
                Unit = Length > 1e-9 ? direction / Length : new Vector2d(1.0, 0.0);
                LeftNormal = new Vector2d(-Unit.Y, Unit.X);
                Mid = Midpoint(a, b);
            }

            public ObjectId Id { get; }
            public string Layer { get; }
            public Point2d A { get; }
            public Point2d B { get; }
            public bool Horizontal { get; }
            public bool Vertical { get; }
            public double Length { get; }
            public Vector2d Unit { get; }
            public Vector2d LeftNormal { get; }
            public Point2d Mid { get; }
        }
    }
}
