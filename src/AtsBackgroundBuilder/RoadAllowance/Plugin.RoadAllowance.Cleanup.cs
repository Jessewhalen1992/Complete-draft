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
        private static void NormalizeGeneratedRoadAllowanceLayers(
            Database database,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
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
                const double lengthMin = 4.0;

                var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var generatedSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
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

                    var len = a.GetDistanceTo(b);
                    if (len < lengthMin)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        generatedSegments.Add((id, a, b, horizontal, vertical, len));
                    }
                }

                if (generatedSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var normalizedGenerated = 0;
                for (var i = 0; i < generatedSegments.Count; i++)
                {
                    var seg = generatedSegments[i];
                    if (!(tr.GetObject(seg.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (IsUsecLayer(writable.Layer) ||
                        string.Equals(writable.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = LayerUsecBase;
                    writable.ColorIndex = 256;
                    normalizedGenerated++;
                }

                tr.Commit();
                if (normalizedGenerated > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized {normalizedGenerated} generated RA segment(s) with invalid layer to L-USEC base [candidates={generatedSegments.Count}].");
                }
            }
        }

        private static void SnapContextEndpointsAfterTrim(
            Database database,
            IReadOnlyCollection<ObjectId> contextSectionIds,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || contextSectionIds == null || contextSectionIds.Count == 0 || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }
            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);

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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryWriteOpenSegment(Entity ent, Point2d a, Point2d b)
                {
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (ent is Line ln)
                    {
                        ln.StartPoint = new Point3d(a.X, a.Y, ln.StartPoint.Z);
                        ln.EndPoint = new Point3d(b.X, b.Y, ln.EndPoint.Z);
                        return true;
                    }

                    if (ent is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        pl.SetPointAt(0, a);
                        pl.SetPointAt(1, b);
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

                var contextSet = new HashSet<ObjectId>(contextSectionIds.Where(id => !id.IsNull));
                var endpointAnchors = new List<(ObjectId Id, string Layer, bool IsContext, Point2d P)>();
                var allSegments = new List<(ObjectId Id, string Layer, bool IsContext, Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
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

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !IsUsecLayer(ent.Layer))
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

                    var isContext = contextSet.Contains(id);
                    var layerName = ent.Layer ?? string.Empty;
                    endpointAnchors.Add((id, layerName, isContext, a));
                    endpointAnchors.Add((id, layerName, isContext, b));
                    allSegments.Add((id, layerName, isContext, a, b, IsHorizontalLike(a, b), IsVerticalLike(a, b)));
                }

                if (endpointAnchors.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointSnapTol = 1.20;
                const double segmentSnapTol = 0.60;
                const double moveTol = 0.02;
                const double axisTol = 1.20;
                var adjusted = 0;
                foreach (var id in contextSet.ToList())
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var p0, out var p1))
                    {
                        continue;
                    }

                    var currentLayer = ent.Layer ?? string.Empty;
                    var thisIsHorizontal = IsHorizontalLike(p0, p1);
                    var thisIsVertical = IsVerticalLike(p0, p1);

                    bool TrySnapEndpoint(Point2d endpoint, Point2d oppositeEndpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDist = double.MaxValue;
                        var bestTargetIsContext = true;
                        var ownerDirVec = oppositeEndpoint - endpoint;
                        var ownerDirLen = ownerDirVec.Length;
                        if (ownerDirLen <= 1e-6)
                        {
                            return false;
                        }

                        var ownerDir = ownerDirVec / ownerDirLen;
                        for (var i = 0; i < endpointAnchors.Count; i++)
                        {
                            var anchor = endpointAnchors[i];
                            if (anchor.Id == id)
                            {
                                continue;
                            }

                            if (!string.Equals(anchor.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (thisIsHorizontal && Math.Abs(anchor.P.Y - endpoint.Y) > axisTol)
                            {
                                continue;
                            }

                            if (thisIsVertical && Math.Abs(anchor.P.X - endpoint.X) > axisTol)
                            {
                                continue;
                            }

                            var candidate = anchor.P;
                            if (thisIsHorizontal)
                            {
                                candidate = new Point2d(anchor.P.X, endpoint.Y);
                            }
                            else if (thisIsVertical)
                            {
                                candidate = new Point2d(endpoint.X, anchor.P.Y);
                            }

                            var d = endpoint.GetDistanceTo(candidate);
                            if (d > endpointSnapTol)
                            {
                                continue;
                            }

                            var prefer = !anchor.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = anchor.IsContext;
                            snapped = candidate;
                        }

                        // Also allow endpoint to snap onto nearby segment span
                        // (axis-constrained) to eliminate tiny overlaps/gaps where anchor
                        // endpoints are not the closest geometry.
                        for (var i = 0; i < allSegments.Count; i++)
                        {
                            var seg = allSegments[i];
                            if (seg.Id == id)
                            {
                                continue;
                            }

                            if (!string.Equals(seg.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (thisIsHorizontal && !seg.Horizontal)
                            {
                                continue;
                            }

                            if (thisIsVertical && !seg.Vertical)
                            {
                                continue;
                            }

                            Point2d candidate;
                            if (thisIsHorizontal)
                            {
                                var yLine = 0.5 * (seg.A.Y + seg.B.Y);
                                if (Math.Abs(yLine - endpoint.Y) > axisTol)
                                {
                                    continue;
                                }

                                var minX = Math.Min(seg.A.X, seg.B.X);
                                var maxX = Math.Max(seg.A.X, seg.B.X);
                                var x = Math.Max(minX, Math.Min(maxX, endpoint.X));
                                candidate = new Point2d(x, endpoint.Y);
                            }
                            else if (thisIsVertical)
                            {
                                var xLine = 0.5 * (seg.A.X + seg.B.X);
                                if (Math.Abs(xLine - endpoint.X) > axisTol)
                                {
                                    continue;
                                }

                                var minY = Math.Min(seg.A.Y, seg.B.Y);
                                var maxY = Math.Max(seg.A.Y, seg.B.Y);
                                var y = Math.Max(minY, Math.Min(maxY, endpoint.Y));
                                candidate = new Point2d(endpoint.X, y);
                            }
                            else
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(candidate);
                            if (d > segmentSnapTol)
                            {
                                continue;
                            }

                            var prefer = !seg.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = seg.IsContext;
                            snapped = candidate;
                        }

                        // Orientation-based fallback for rotated local geometry where
                        // world X/Y axis checks are not sufficient.
                        const double collinearDotMin = 0.995;
                        const double perpendicularDotMax = 0.10;
                        const double collinearOffsetTol = 0.85;

                        for (var i = 0; i < endpointAnchors.Count; i++)
                        {
                            var anchor = endpointAnchors[i];
                            if (anchor.Id == id)
                            {
                                continue;
                            }

                            if (!string.Equals(anchor.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var t = (anchor.P - endpoint).DotProduct(ownerDir);
                            var candidate = endpoint + (ownerDir * t);
                            var d = endpoint.GetDistanceTo(candidate);
                            if (d > endpointSnapTol)
                            {
                                continue;
                            }

                            var lateral = candidate.GetDistanceTo(anchor.P);
                            if (lateral > collinearOffsetTol)
                            {
                                continue;
                            }

                            var prefer = !anchor.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = anchor.IsContext;
                            snapped = candidate;
                        }

                        for (var i = 0; i < allSegments.Count; i++)
                        {
                            var seg = allSegments[i];
                            if (seg.Id == id)
                            {
                                continue;
                            }

                            if (!string.Equals(seg.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var otherVec = seg.B - seg.A;
                            var otherLen = otherVec.Length;
                            if (otherLen <= 1e-6)
                            {
                                continue;
                            }

                            var otherDir = otherVec / otherLen;
                            var cosAbs = Math.Abs(ownerDir.DotProduct(otherDir));
                            Point2d candidate;
                            double d;
                            if (cosAbs >= collinearDotMin)
                            {
                                var cA = endpoint + (ownerDir * ((seg.A - endpoint).DotProduct(ownerDir)));
                                var cB = endpoint + (ownerDir * ((seg.B - endpoint).DotProduct(ownerDir)));
                                var dA = endpoint.GetDistanceTo(cA);
                                var dB = endpoint.GetDistanceTo(cB);
                                candidate = dA <= dB ? cA : cB;
                                d = dA <= dB ? dA : dB;
                                var raw = dA <= dB ? seg.A : seg.B;
                                if (candidate.GetDistanceTo(raw) > collinearOffsetTol || d > endpointSnapTol)
                                {
                                    continue;
                                }
                            }
                            else if (cosAbs <= perpendicularDotMax)
                            {
                                if (!TryIntersectInfiniteLineWithSegment(endpoint, ownerDir, seg.A, seg.B, out var tCross))
                                {
                                    continue;
                                }

                                candidate = endpoint + (ownerDir * tCross);
                                d = Math.Abs(tCross);
                                if (d > segmentSnapTol)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                continue;
                            }

                            var prefer = !seg.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = seg.IsContext;
                            snapped = candidate;
                        }

                        return found;
                    }

                    var new0 = p0;
                    var new1 = p1;
                    TrySnapEndpoint(p0, p1, out new0);
                    TrySnapEndpoint(p1, p0, out new1);

                    if (new0.GetDistanceTo(p0) <= moveTol && new1.GetDistanceTo(p1) <= moveTol)
                    {
                        continue;
                    }

                    if (TryWriteOpenSegment(ent, new0, new1))
                    {
                        adjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: snapped {adjusted} context segment endpoint(s) after 100m trim.");
                }
            }
        }

        private static void HealBufferedBoundaryEndpointSeams(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
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
            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);

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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryWriteOpenSegment(Entity ent, Point2d a, Point2d b)
                {
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (ent is Line ln)
                    {
                        ln.StartPoint = new Point3d(a.X, a.Y, ln.StartPoint.Z);
                        ln.EndPoint = new Point3d(b.X, b.Y, ln.EndPoint.Z);
                        return true;
                    }

                    if (ent is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        pl.SetPointAt(0, a);
                        pl.SetPointAt(1, b);
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

                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
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

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !IsUsecLayer(ent.Layer))
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

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointSnapTol = 1.60;
                const double crossSnapTol = 1.60;
                const double axisTol = 1.50;
                const double moveTol = 0.01;
                var adjusted = 0;
                for (var si = 0; si < segments.Count; si++)
                {
                    var seg = segments[si];
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(seg.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var p0, out var p1))
                    {
                        continue;
                    }

                    var isHorizontal = IsHorizontalLike(p0, p1);
                    var isVertical = IsVerticalLike(p0, p1);
                    if (!isHorizontal && !isVertical)
                    {
                        continue;
                    }

                    bool TrySnapEndpoint(Point2d endpoint, Point2d oppositeEndpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDistance = double.MaxValue;
                        var bestSameLayer = false;
                        var ownerDirVec = oppositeEndpoint - endpoint;
                        var ownerDirLen = ownerDirVec.Length;
                        if (ownerDirLen <= 1e-6)
                        {
                            return false;
                        }

                        var ownerDir = ownerDirVec / ownerDirLen;

                        // 1) Axis-constrained endpoint-to-endpoint snap (closes small collinear gaps).
                        for (var oi = 0; oi < segments.Count; oi++)
                        {
                            if (oi == si)
                            {
                                continue;
                            }

                            var other = segments[oi];
                            if (isHorizontal && !other.Horizontal)
                            {
                                continue;
                            }

                            if (isVertical && !other.Vertical)
                            {
                                continue;
                            }

                            var sameLayer = string.Equals(seg.Layer, other.Layer, StringComparison.OrdinalIgnoreCase);
                            if (!sameLayer)
                            {
                                continue;
                            }
                            if (isHorizontal)
                            {
                                var otherY = 0.5 * (other.A.Y + other.B.Y);
                                if (Math.Abs(otherY - endpoint.Y) > axisTol)
                                {
                                    continue;
                                }

                                var c1 = new Point2d(other.A.X, endpoint.Y);
                                var c2 = new Point2d(other.B.X, endpoint.Y);
                                var d1 = endpoint.GetDistanceTo(c1);
                                var d2 = endpoint.GetDistanceTo(c2);
                                var candidate = d1 <= d2 ? c1 : c2;
                                var d = d1 <= d2 ? d1 : d2;
                                if (d > endpointSnapTol)
                                {
                                    continue;
                                }

                                if (!found ||
                                    (sameLayer && !bestSameLayer) ||
                                    (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                {
                                    found = true;
                                    bestDistance = d;
                                    bestSameLayer = sameLayer;
                                    snapped = candidate;
                                }
                            }
                            else if (isVertical)
                            {
                                var otherX = 0.5 * (other.A.X + other.B.X);
                                if (Math.Abs(otherX - endpoint.X) > axisTol)
                                {
                                    continue;
                                }

                                var c1 = new Point2d(endpoint.X, other.A.Y);
                                var c2 = new Point2d(endpoint.X, other.B.Y);
                                var d1 = endpoint.GetDistanceTo(c1);
                                var d2 = endpoint.GetDistanceTo(c2);
                                var candidate = d1 <= d2 ? c1 : c2;
                                var d = d1 <= d2 ? d1 : d2;
                                if (d > endpointSnapTol)
                                {
                                    continue;
                                }

                                if (!found ||
                                    (sameLayer && !bestSameLayer) ||
                                    (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                {
                                    found = true;
                                    bestDistance = d;
                                    bestSameLayer = sameLayer;
                                    snapped = candidate;
                                }
                            }
                        }

                        // 2) Endpoint-to-perpendicular-segment snap (clips tiny overhangs/under-runs at crosses).
                        for (var oi = 0; oi < segments.Count; oi++)
                        {
                            if (oi == si)
                            {
                                continue;
                            }

                            var other = segments[oi];
                            var sameLayer = string.Equals(seg.Layer, other.Layer, StringComparison.OrdinalIgnoreCase);
                            if (!sameLayer)
                            {
                                continue;
                            }
                            if (isHorizontal)
                            {
                                if (!other.Vertical)
                                {
                                    continue;
                                }

                                var xLine = 0.5 * (other.A.X + other.B.X);
                                var d = Math.Abs(xLine - endpoint.X);
                                if (d > crossSnapTol)
                                {
                                    continue;
                                }

                                var minY = Math.Min(other.A.Y, other.B.Y) - axisTol;
                                var maxY = Math.Max(other.A.Y, other.B.Y) + axisTol;
                                if (endpoint.Y < minY || endpoint.Y > maxY)
                                {
                                    continue;
                                }

                                var candidate = new Point2d(xLine, endpoint.Y);
                                if (!found ||
                                    (sameLayer && !bestSameLayer) ||
                                    (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                {
                                    found = true;
                                    bestDistance = d;
                                    bestSameLayer = sameLayer;
                                    snapped = candidate;
                                }
                            }
                            else if (isVertical)
                            {
                                if (!other.Horizontal)
                                {
                                    continue;
                                }

                                var yLine = 0.5 * (other.A.Y + other.B.Y);
                                var d = Math.Abs(yLine - endpoint.Y);
                                if (d > crossSnapTol)
                                {
                                    continue;
                                }

                                var minX = Math.Min(other.A.X, other.B.X) - axisTol;
                                var maxX = Math.Max(other.A.X, other.B.X) + axisTol;
                                if (endpoint.X < minX || endpoint.X > maxX)
                                {
                                    continue;
                                }

                                var candidate = new Point2d(endpoint.X, yLine);
                                if (!found ||
                                    (sameLayer && !bestSameLayer) ||
                                    (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                {
                                    found = true;
                                    bestDistance = d;
                                    bestSameLayer = sameLayer;
                                    snapped = candidate;
                                }
                            }
                        }

                        // 3) Orientation-based fallback (independent of world X/Y):
                        //    - collinear endpoint snap
                        //    - perpendicular segment intersection snap
                        const double collinearDotMin = 0.995;
                        const double perpendicularDotMax = 0.10;
                        const double collinearOffsetTol = 0.85;
                        for (var oi = 0; oi < segments.Count; oi++)
                        {
                            if (oi == si)
                            {
                                continue;
                            }

                            var other = segments[oi];
                            var sameLayer = string.Equals(seg.Layer, other.Layer, StringComparison.OrdinalIgnoreCase);
                            if (!sameLayer)
                            {
                                continue;
                            }
                            var otherVec = other.B - other.A;
                            var otherLen = otherVec.Length;
                            if (otherLen <= 1e-6)
                            {
                                continue;
                            }

                            var otherDir = otherVec / otherLen;
                            var cosAbs = Math.Abs(ownerDir.DotProduct(otherDir));

                            if (cosAbs >= collinearDotMin)
                            {
                                var colCandidates = new[] { other.A, other.B };
                                for (var ci = 0; ci < colCandidates.Length; ci++)
                                {
                                    var c = colCandidates[ci];
                                    var lateral = DistancePointToInfiniteLine(c, endpoint, endpoint + ownerDir);
                                    if (lateral > collinearOffsetTol)
                                    {
                                        continue;
                                    }

                                    var t = (c - endpoint).DotProduct(ownerDir);
                                    var d = Math.Abs(t);
                                    if (d > endpointSnapTol)
                                    {
                                        continue;
                                    }

                                    var candidate = endpoint + (ownerDir * t);
                                    if (!found ||
                                        (sameLayer && !bestSameLayer) ||
                                        (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                    {
                                        found = true;
                                        bestDistance = d;
                                        bestSameLayer = sameLayer;
                                        snapped = candidate;
                                    }
                                }
                            }

                            if (cosAbs <= perpendicularDotMax)
                            {
                                if (!TryIntersectInfiniteLineWithSegment(endpoint, ownerDir, other.A, other.B, out var t))
                                {
                                    continue;
                                }

                                var d = Math.Abs(t);
                                if (d > crossSnapTol)
                                {
                                    continue;
                                }

                                var candidate = endpoint + (ownerDir * t);
                                if (!found ||
                                    (sameLayer && !bestSameLayer) ||
                                    (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                {
                                    found = true;
                                    bestDistance = d;
                                    bestSameLayer = sameLayer;
                                    snapped = candidate;
                                }
                            }
                        }

                        return found;
                    }

                    var new0 = p0;
                    var new1 = p1;
                    TrySnapEndpoint(p0, p1, out new0);
                    TrySnapEndpoint(p1, p0, out new1);

                    if (new0.GetDistanceTo(p0) <= moveTol && new1.GetDistanceTo(p1) <= moveTol)
                    {
                        continue;
                    }

                    if (!TryWriteOpenSegment(ent, new0, new1))
                    {
                        continue;
                    }

                    adjusted++;
                    segments[si] = (seg.Id, seg.Layer, new0, new1, IsHorizontalLike(new0, new1), IsVerticalLike(new0, new1));
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: seam-healed {adjusted} L-SEC/L-USEC segment(s) near buffered boundary endpoints.");
            }
        }

        private static void CloseTinyRoadAllowanceCornerGaps(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId>? generatedRoadAllowanceIds,
            Logger? logger)
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
                ? new HashSet<ObjectId>(generatedRoadAllowanceIds)
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryMoveEndpointByIndex(Entity writable, int endpointIndex, Point2d target, double moveTol)
                {
                    if (endpointIndex != 0 && endpointIndex != 1)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var old = endpointIndex == 0
                            ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                            : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        if (endpointIndex == 0)
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
                        var old = pl.GetPoint2dAt(endpointIndex);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        pl.SetPointAt(endpointIndex, target);
                        return true;
                    }

                    return false;
                }

                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool Generated)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
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

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical, generatedSet.Contains(id)));
                }

                if (segments.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                var adjusted = 0;
                const double tinyGap = 1.25;
                const double usecTwentyTwelveGap = 1.25;
                const double usecExtendedMinMajorGap = 2.00;
                const double moveTol = 0.01;
                var movedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string EndpointKey(int segIndex, int endpointIndex)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}:{1}",
                        segIndex,
                        endpointIndex);
                }

                double AxisOverlap(
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool Generated) a,
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool Generated) b)
                {
                    if (a.Horizontal && b.Horizontal)
                    {
                        var aMin = Math.Min(a.A.X, a.B.X);
                        var aMax = Math.Max(a.A.X, a.B.X);
                        var bMin = Math.Min(b.A.X, b.B.X);
                        var bMax = Math.Max(b.A.X, b.B.X);
                        return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                    }

                    if (a.Vertical && b.Vertical)
                    {
                        var aMin = Math.Min(a.A.Y, a.B.Y);
                        var aMax = Math.Max(a.A.Y, a.B.Y);
                        var bMin = Math.Min(b.A.Y, b.B.Y);
                        var bMax = Math.Max(b.A.Y, b.B.Y);
                        return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                    }

                    return 0.0;
                }

                bool HasCompanionAtOffset(
                    int segIndex,
                    double expectedOffset,
                    double tol,
                    double minOverlap,
                    bool requireSameLayer,
                    bool? requireCompanionGenerated = null)
                {
                    if (segIndex < 0 || segIndex >= segments.Count)
                    {
                        return false;
                    }

                    var s = segments[segIndex];
                    if (!s.Horizontal && !s.Vertical)
                    {
                        return false;
                    }

                    var sCoord = s.Horizontal
                        ? (0.5 * (s.A.Y + s.B.Y))
                        : (0.5 * (s.A.X + s.B.X));
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segIndex)
                        {
                            continue;
                        }

                        var o = segments[oi];
                        if (s.Horizontal != o.Horizontal || s.Vertical != o.Vertical)
                        {
                            continue;
                        }

                        if (requireSameLayer &&
                            !string.Equals(s.Layer, o.Layer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (requireCompanionGenerated.HasValue && o.Generated != requireCompanionGenerated.Value)
                        {
                            continue;
                        }

                        if (AxisOverlap(s, o) < minOverlap)
                        {
                            continue;
                        }

                        var oCoord = o.Horizontal
                            ? (0.5 * (o.A.Y + o.B.Y))
                            : (0.5 * (o.A.X + o.B.X));
                        var offset = Math.Abs(oCoord - sCoord);
                        if (Math.Abs(offset - expectedOffset) <= tol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var usecInnerTwentyTwelveStrict = new bool[segments.Count];
                var usecInnerTwentyTwelveLoose = new bool[segments.Count];
                var strictInnerCount = 0;
                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (!string.Equals(s.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Inner 20.11-in-30.16 indicator.
                    // Loose: previous geometry-only heuristic.
                    // Strict: requires a non-generated 20.11 companion so inner 20.11 is chosen
                    // over outer 30.16 when both are nearby.
                    var hasTenCompanion = HasCompanionAtOffset(i, 10.06, 1.75, 10.0, requireSameLayer: true);
                    var hasTwentyCompanion = HasCompanionAtOffset(i, 20.11, 2.50, 10.0, requireSameLayer: false);
                    var looseInner = hasTenCompanion && hasTwentyCompanion;
                    usecInnerTwentyTwelveLoose[i] = looseInner;

                    var strictInner = false;
                    if (looseInner && generatedSet.Count > 0)
                    {
                        var hasTwentyNonGeneratedCompanion = HasCompanionAtOffset(
                            i,
                            20.11,
                            2.50,
                            10.0,
                            requireSameLayer: false,
                            requireCompanionGenerated: false);
                        strictInner = hasTwentyNonGeneratedCompanion;
                    }

                    usecInnerTwentyTwelveStrict[i] = strictInner;
                    if (strictInner)
                    {
                        strictInnerCount++;
                    }
                }

                var usecInnerTwentyTwelve = strictInnerCount > 0
                    ? usecInnerTwentyTwelveStrict
                    : usecInnerTwentyTwelveLoose;

                for (var hi = 0; hi < segments.Count; hi++)
                {
                    var hSeg = segments[hi];
                    if (!hSeg.Horizontal)
                    {
                        continue;
                    }

                    for (var hEnd = 0; hEnd <= 1; hEnd++)
                    {
                        var hKey = EndpointKey(hi, hEnd);
                        if (movedEndpoints.Contains(hKey))
                        {
                            continue;
                        }

                        var hPoint = hEnd == 0 ? hSeg.A : hSeg.B;
                        var hInnerTwentyTwelve = usecInnerTwentyTwelve[hi];
                        var bestFound = false;
                        var bestScore = double.MaxValue;
                        var bestVi = -1;
                        var bestVEnd = -1;
                        var bestTarget = default(Point2d);
                        for (var vi = 0; vi < segments.Count; vi++)
                        {
                            if (vi == hi)
                            {
                                continue;
                            }

                            var vSeg = segments[vi];
                            if (!vSeg.Vertical)
                            {
                                continue;
                            }

                            // Allow joins when at least one corridor leg is generated.
                            // We still only move generated endpoints below.
                            if (!hSeg.Generated && !vSeg.Generated)
                            {
                                continue;
                            }

                            for (var vEnd = 0; vEnd <= 1; vEnd++)
                            {
                                var vKey = EndpointKey(vi, vEnd);
                                if (movedEndpoints.Contains(vKey))
                                {
                                    continue;
                                }

                                var vPoint = vEnd == 0 ? vSeg.A : vSeg.B;
                                var vInnerTwentyTwelve = usecInnerTwentyTwelve[vi];
                                // Keep this pass to tiny generated corner closes only.
                                // Larger 20.11/30.16 corridor joins are handled by dedicated passes.
                                var allowExtendedUsecJoin = false;

                                if (!TryIntersectInfiniteLines(hSeg.A, hSeg.B, vSeg.A, vSeg.B, out var target))
                                {
                                    continue;
                                }
                                var dH = hPoint.GetDistanceTo(target);
                                var dV = vPoint.GetDistanceTo(target);
                                var endpointGapLimit = allowExtendedUsecJoin ? usecTwentyTwelveGap : tinyGap;
                                if (dH > endpointGapLimit || dV > endpointGapLimit)
                                {
                                    continue;
                                }

                                var majorGap = Math.Max(dH, dV);
                                if (allowExtendedUsecJoin && majorGap <= usecExtendedMinMajorGap)
                                {
                                    // Prevent tiny snaps from stealing the endpoint needed for the 20.11 corner join.
                                    continue;
                                }

                                var sameLayer = string.Equals(hSeg.Layer, vSeg.Layer, StringComparison.OrdinalIgnoreCase);
                                var score =
                                    dH +
                                    dV -
                                    (sameLayer ? 0.05 : 0.0) -
                                    (allowExtendedUsecJoin ? 0.10 : 0.0) +
                                    (allowExtendedUsecJoin ? (0.15 * Math.Abs(majorGap - 10.06)) : 0.0);
                                if (!bestFound || score < (bestScore - 1e-9))
                                {
                                    bestFound = true;
                                    bestScore = score;
                                    bestVi = vi;
                                    bestVEnd = vEnd;
                                    bestTarget = target;
                                }
                            }
                        }

                        if (!bestFound || bestVi < 0 || bestVEnd < 0)
                        {
                            continue;
                        }

                        var hWritable = tr.GetObject(hSeg.Id, OpenMode.ForWrite, false) as Entity;
                        var vSegBest = segments[bestVi];
                        var vWritable = tr.GetObject(vSegBest.Id, OpenMode.ForWrite, false) as Entity;
                        if (hWritable == null || hWritable.IsErased || vWritable == null || vWritable.IsErased)
                        {
                            continue;
                        }

                        // Keep baseline section grid stable:
                        // this corner-gap pass should only pull generated RA geometry,
                        // never non-generated SEC/USEC endpoints.
                        var allowMoveH = hSeg.Generated;
                        var allowMoveV = vSegBest.Generated;
                        if (!allowMoveH && !allowMoveV)
                        {
                            continue;
                        }

                        var movedH = allowMoveH && TryMoveEndpointByIndex(hWritable, hEnd, bestTarget, moveTol);
                        var movedV = allowMoveV && TryMoveEndpointByIndex(vWritable, bestVEnd, bestTarget, moveTol);
                        if (!movedH && !movedV)
                        {
                            continue;
                        }

                        adjusted++;
                        movedEndpoints.Add(hKey);
                        movedEndpoints.Add(EndpointKey(bestVi, bestVEnd));

                        if (TryReadOpenSegment(hWritable, out var newHA, out var newHB))
                        {
                            segments[hi] = (hSeg.Id, hSeg.Layer, newHA, newHB, IsHorizontalLike(newHA, newHB), IsVerticalLike(newHA, newHB), hSeg.Generated);
                        }

                        if (TryReadOpenSegment(vWritable, out var newVA, out var newVB))
                        {
                            segments[bestVi] = (vSegBest.Id, vSegBest.Layer, newVA, newVB, IsHorizontalLike(newVA, newVB), IsVerticalLike(newVA, newVB), vSegBest.Generated);
                        }
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: corner-gap-closed {adjusted} orthogonal endpoint gap(s) on L-SEC/L-USEC.");
                }
            }
        }

        private static void EnforceSecToUsecTwentyTwelveEndpointRule(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            var disableLegacyRule = true;
            if (disableLegacyRule)
            {
                logger?.WriteLine("Cleanup: EnforceSecToUsecTwentyTwelveEndpointRule disabled by canonical RA endpoint constraints.");
                return;
            }

            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryMoveEndpointByIndex(Entity writable, int endpointIndex, Point2d target, double moveTol)
                {
                    if (endpointIndex != 0 && endpointIndex != 1)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var old = endpointIndex == 0
                            ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                            : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        if (endpointIndex == 0)
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
                        var old = pl.GetPoint2dAt(endpointIndex);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        pl.SetPointAt(endpointIndex, target);
                        return true;
                    }

                    return false;
                }

                void CollectUsecSegments(Entity ent, List<(Point2d A, Point2d B)> destination)
                {
                    if (ent is Line ln)
                    {
                        var a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        var b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (a.GetDistanceTo(b) > 1e-4 && DoesSegmentIntersectAnyWindow(a, b))
                        {
                            destination.Add((a, b));
                        }

                        return;
                    }

                    if (!(ent is Polyline pl) || pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return;
                    }

                    for (var vi = 0; vi < pl.NumberOfVertices - 1; vi++)
                    {
                        var a = pl.GetPoint2dAt(vi);
                        var b = pl.GetPoint2dAt(vi + 1);
                        if (a.GetDistanceTo(b) <= 1e-4)
                        {
                            continue;
                        }

                        if (!DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        destination.Add((a, b));
                    }
                }

                bool TryIntersectRayWithUsec(
                    Point2d rayOrigin,
                    Vector2d rayDirUnit,
                    Point2d segA,
                    Point2d segB,
                    out double t,
                    out Point2d target)
                {
                    t = 0.0;
                    target = rayOrigin;

                    if (TryIntersectInfiniteLineWithSegment(rayOrigin, rayDirUnit, segA, segB, out var tOnRay))
                    {
                        if (tOnRay > 1e-6)
                        {
                            t = tOnRay;
                            target = rayOrigin + (rayDirUnit * tOnRay);
                            return true;
                        }
                    }

                    var db = segB - segA;
                    if (db.Length <= 1e-6)
                    {
                        return false;
                    }

                    var denom = Cross2d(rayDirUnit, db);
                    if (Math.Abs(denom) <= 1e-9)
                    {
                        return false;
                    }

                    var diff = segA - rayOrigin;
                    var tInfinite = Cross2d(diff, db) / denom;
                    if (tInfinite <= 1e-6)
                    {
                        return false;
                    }

                    var p = rayOrigin + (rayDirUnit * tInfinite);
                    const double usecLineGapTol = 1.5;
                    if (DistancePointToSegment(p, segA, segB) > usecLineGapTol)
                    {
                        return false;
                    }

                    t = tInfinite;
                    target = p;
                    return true;
                }

                Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
                {
                    var ab = b - a;
                    var len2 = ab.DotProduct(ab);
                    if (len2 <= 1e-12)
                    {
                        return a;
                    }

                    var t = (p - a).DotProduct(ab) / len2;
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    return a + (ab * t);
                }

                var secSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
                var usecSegments = new List<(Point2d A, Point2d B)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        CollectUsecSegments(ent, usecSegments);
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

                    if (string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        var horizontal = IsHorizontalLike(a, b);
                        var vertical = IsVerticalLike(a, b);
                        if (!horizontal && !vertical)
                        {
                            continue;
                        }

                        secSegments.Add((id, a, b, horizontal, vertical));
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) &&
                        IsAdjustableLsdLineSegment(a, b))
                    {
                        lsdSegments.Add((id, a, b));
                    }
                }

                if (secSegments.Count == 0 || usecSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double touchTol = 0.30;
                const double inwardCompanionTarget = CorrectionLinePairGapMeters;
                const double inwardCompanionAltTarget = 20.11;
                const double inwardCompanionMin = 5.0;
                const double inwardCompanionMax = 26.5;
                const double inwardApproxPerpTol = 4.5;
                const double inwardApproxPointTol = 10.0;
                const double inwardInfiniteScoreTol = 4.0;
                const double inwardProjectedPerpTol = 12.0;
                const double inwardProjectedGapTol = 20.0;
                const double endpointMoveTol = 0.05;
                const double lsdAnchorTol = 0.40;
                const double lsdMaxMove = 15.0;

                var scannedEndpoints = 0;
                var touchingUsecEndpoints = 0;
                var candidateEndpoints = 0;
                var adjusted = 0;
                var endpointMoves = new List<(Point2d Old, Point2d New)>();
                double ScoreToExpected(double t)
                {
                    return Math.Min(
                        Math.Abs(t - inwardCompanionTarget),
                        Math.Abs(t - inwardCompanionAltTarget));
                }

                for (var si = 0; si < secSegments.Count; si++)
                {
                    var sec = secSegments[si];
                    if (!sec.Horizontal)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(sec.Id, OpenMode.ForWrite, false) is Entity writableSec) || writableSec.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableSec, out var p0, out var p1))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        scannedEndpoints++;

                        var endpoint = endpointIndex == 0 ? p0 : p1;
                        var other = endpointIndex == 0 ? p1 : p0;
                        var inward = other - endpoint;
                        var inwardLen = inward.Length;
                        if (inwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var inwardDir = inward / inwardLen;

                        var touchesUsec = false;
                        for (var ui = 0; ui < usecSegments.Count; ui++)
                        {
                            var u = usecSegments[ui];
                            if (DistancePointToSegment(endpoint, u.A, u.B) <= touchTol)
                            {
                                touchesUsec = true;
                                break;
                            }
                        }

                        if (!touchesUsec)
                        {
                            continue;
                        }

                        touchingUsecEndpoints++;

                        var expectedPoint = endpoint + (inwardDir * inwardCompanionTarget);
                        var rayRef = endpoint + inwardDir;
                        double? bestExactT = null;
                        var bestExactTarget = endpoint;
                        var bestExactScore = double.MaxValue;
                        double? bestApproxT = null;
                        var bestApproxTarget = endpoint;
                        var bestApproxScore = double.MaxValue;
                        for (var ui = 0; ui < usecSegments.Count; ui++)
                        {
                            var u = usecSegments[ui];
                            if (DistancePointToSegment(endpoint, u.A, u.B) <= touchTol)
                            {
                                continue;
                            }

                            if (!TryIntersectRayWithUsec(endpoint, inwardDir, u.A, u.B, out var t, out var target))
                            {
                                continue;
                            }

                            if (t < inwardCompanionMin || t > inwardCompanionMax)
                            {
                                continue;
                            }

                            var score = ScoreToExpected(t);
                            if (!bestExactT.HasValue ||
                                score < (bestExactScore - 1e-9) ||
                                (Math.Abs(score - bestExactScore) <= 1e-9 && t < bestExactT.Value))
                            {
                                bestExactT = t;
                                bestExactTarget = target;
                                bestExactScore = score;
                            }

                            // Fallback for split/slightly-misaligned township-change geometry:
                            // snap near the expected inward 10.06 point even when exact segment
                            // intersection is not robust.
                            var approxPoint = ClosestPointOnSegment(expectedPoint, u.A, u.B);
                            var approxT = (approxPoint - endpoint).DotProduct(inwardDir);
                            if (approxT < inwardCompanionMin || approxT > inwardCompanionMax)
                            {
                                continue;
                            }

                            var perp = DistancePointToInfiniteLine(approxPoint, endpoint, rayRef);
                            if (perp > inwardApproxPerpTol)
                            {
                                continue;
                            }

                            var gap = approxPoint.GetDistanceTo(expectedPoint);
                            if (gap > inwardApproxPointTol)
                            {
                                continue;
                            }

                            var approxScore = ScoreToExpected(approxT) + (0.25 * perp) + (0.05 * gap);
                            if (!bestApproxT.HasValue ||
                                approxScore < (bestApproxScore - 1e-9) ||
                                (Math.Abs(approxScore - bestApproxScore) <= 1e-9 && approxT < bestApproxT.Value))
                            {
                                bestApproxT = approxT;
                                bestApproxTarget = approxPoint;
                                bestApproxScore = approxScore;
                            }
                        }

                        double? bestInfiniteT = null;
                        var bestInfiniteTarget = endpoint;
                        var bestInfiniteScore = double.MaxValue;
                        if (!bestExactT.HasValue && !bestApproxT.HasValue)
                        {
                            for (var ui = 0; ui < usecSegments.Count; ui++)
                            {
                                var u = usecSegments[ui];
                                if (DistancePointToSegment(endpoint, u.A, u.B) <= touchTol)
                                {
                                    continue;
                                }

                                var segDir = u.B - u.A;
                                var denom = Cross2d(inwardDir, segDir);
                                if (Math.Abs(denom) <= 1e-9)
                                {
                                    continue;
                                }

                                var diff = u.A - endpoint;
                                var tInfinite = Cross2d(diff, segDir) / denom;
                                if (tInfinite < inwardCompanionMin || tInfinite > inwardCompanionMax)
                                {
                                    continue;
                                }

                                var score = ScoreToExpected(tInfinite);
                                if (score > inwardInfiniteScoreTol)
                                {
                                    continue;
                                }

                                if (!bestInfiniteT.HasValue ||
                                    score < (bestInfiniteScore - 1e-9) ||
                                    (Math.Abs(score - bestInfiniteScore) <= 1e-9 && tInfinite < bestInfiniteT.Value))
                                {
                                    bestInfiniteT = tInfinite;
                                    bestInfiniteTarget = endpoint + (inwardDir * tInfinite);
                                    bestInfiniteScore = score;
                                }
                            }
                        }

                        double? bestProjectedT = null;
                        var bestProjectedTarget = endpoint;
                        var bestProjectedScore = double.MaxValue;
                        if (!bestExactT.HasValue && !bestApproxT.HasValue && !bestInfiniteT.HasValue)
                        {
                            var expectedTargets = new[] { inwardCompanionTarget, inwardCompanionAltTarget };
                            for (var dirIndex = 0; dirIndex < 2; dirIndex++)
                            {
                                var dir = dirIndex == 0 ? inwardDir : -inwardDir;
                                var rayRefProj = endpoint + dir;
                                for (var ti = 0; ti < expectedTargets.Length; ti++)
                                {
                                    var expectedDistance = expectedTargets[ti];
                                    var expectedProjPoint = endpoint + (dir * expectedDistance);
                                    for (var ui = 0; ui < usecSegments.Count; ui++)
                                    {
                                        var u = usecSegments[ui];
                                        if (DistancePointToSegment(endpoint, u.A, u.B) <= touchTol)
                                        {
                                            continue;
                                        }

                                        var projected = ClosestPointOnSegment(expectedProjPoint, u.A, u.B);
                                        var tProjected = (projected - endpoint).DotProduct(dir);
                                        if (tProjected < inwardCompanionMin || tProjected > inwardCompanionMax)
                                        {
                                            continue;
                                        }

                                        var perp = DistancePointToInfiniteLine(projected, endpoint, rayRefProj);
                                        if (perp > inwardProjectedPerpTol)
                                        {
                                            continue;
                                        }

                                        var gap = projected.GetDistanceTo(expectedProjPoint);
                                        if (gap > inwardProjectedGapTol)
                                        {
                                            continue;
                                        }

                                        var score = Math.Abs(tProjected - expectedDistance) + (0.20 * perp) + (0.08 * gap);
                                        if (!bestProjectedT.HasValue ||
                                            score < (bestProjectedScore - 1e-9) ||
                                            (Math.Abs(score - bestProjectedScore) <= 1e-9 && tProjected < bestProjectedT.Value))
                                        {
                                            bestProjectedT = tProjected;
                                            bestProjectedTarget = projected;
                                            bestProjectedScore = score;
                                        }
                                    }
                                }
                            }
                        }

                        var bestT = bestExactT ?? bestApproxT ?? bestInfiniteT ?? bestProjectedT;
                        var bestTarget =
                            bestExactT.HasValue ? bestExactTarget :
                            (bestApproxT.HasValue
                                ? bestApproxTarget
                                : (bestInfiniteT.HasValue ? bestInfiniteTarget : bestProjectedTarget));
                        if (!bestT.HasValue)
                        {
                            continue;
                        }

                        candidateEndpoints++;
                        if (!TryMoveEndpointByIndex(writableSec, endpointIndex, bestTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        adjusted++;
                        endpointMoves.Add((endpoint, bestTarget));
                        if (endpointIndex == 0)
                        {
                            p0 = bestTarget;
                        }
                        else
                        {
                            p1 = bestTarget;
                        }
                    }
                }

                var lsdAdjusted = 0;
                if (endpointMoves.Count > 0 && lsdSegments.Count > 0)
                {
                    for (var li = 0; li < lsdSegments.Count; li++)
                    {
                        var lsd = lsdSegments[li];
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var l0, out var l1))
                        {
                            continue;
                        }

                        var targets = new Point2d[] { l0, l1 };
                        var movedIndex0 = false;
                        var movedIndex1 = false;
                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var endpoint = endpointIndex == 0 ? l0 : l1;
                            var found = false;
                            var bestOldDistance = double.MaxValue;
                            var bestTarget = endpoint;
                            for (var mi = 0; mi < endpointMoves.Count; mi++)
                            {
                                var move = endpointMoves[mi];
                                var oldDistance = endpoint.GetDistanceTo(move.Old);
                                if (oldDistance > lsdAnchorTol)
                                {
                                    continue;
                                }

                                var moveDistance = endpoint.GetDistanceTo(move.New);
                                if (moveDistance <= endpointMoveTol || moveDistance > lsdMaxMove)
                                {
                                    continue;
                                }

                                if (oldDistance < bestOldDistance)
                                {
                                    found = true;
                                    bestOldDistance = oldDistance;
                                    bestTarget = move.New;
                                }
                            }

                            if (!found)
                            {
                                continue;
                            }

                            targets[endpointIndex] = bestTarget;
                            if (endpointIndex == 0)
                            {
                                movedIndex0 = true;
                            }
                            else
                            {
                                movedIndex1 = true;
                            }
                        }

                        if (!movedIndex0 && !movedIndex1)
                        {
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (movedIndex0)
                            {
                                lsdLine.StartPoint = new Point3d(targets[0].X, targets[0].Y, lsdLine.StartPoint.Z);
                                lsdAdjusted++;
                            }

                            if (movedIndex1)
                            {
                                lsdLine.EndPoint = new Point3d(targets[1].X, targets[1].Y, lsdLine.EndPoint.Z);
                                lsdAdjusted++;
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            if (movedIndex0)
                            {
                                lsdPoly.SetPointAt(0, targets[0]);
                                lsdAdjusted++;
                            }

                            if (movedIndex1)
                            {
                                lsdPoly.SetPointAt(1, targets[1]);
                                lsdAdjusted++;
                            }
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: enforced L-SEC->L-USEC 20.11 endpoint rule adjusted={adjusted}, scanned={scannedEndpoints}, touchingUsec={touchingUsecEndpoints}, withInwardCompanion={candidateEndpoints}.");
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to follow enforced 20.11 endpoint moves.");
                }
            }
        }

        private static void ApplySimpleWestEndUsecExtensionRule(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId>? generatedRoadAllowanceIds,
            Logger? logger)
        {
            var disableLegacyRule = true;
            if (disableLegacyRule)
            {
                logger?.WriteLine("Cleanup: simple west-end L-SEC->L-USEC rule disabled by canonical RA endpoint constraints.");
                return;
            }

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
            if (generatedSet.Count == 0)
            {
                logger?.WriteLine("Cleanup: simple west-end rule skipped (no generated RA ids).");
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryMoveEndpointByIndex(Entity writable, int endpointIndex, Point2d target, double moveTol)
                {
                    if (endpointIndex != 0 && endpointIndex != 1)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var old = endpointIndex == 0
                            ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                            : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        if (endpointIndex == 0)
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
                        var idx = endpointIndex == 0 ? 0 : pl.NumberOfVertices - 1;
                        var old = pl.GetPoint2dAt(idx);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        pl.SetPointAt(idx, target);
                        return true;
                    }

                    return false;
                }

                void CollectVerticalRoadSegments(Entity ent, List<(Point2d A, Point2d B)> destination)
                {
                    if (ent is Line ln)
                    {
                        var a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        var b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (a.GetDistanceTo(b) > 1e-4 &&
                            IsVerticalLike(a, b) &&
                            DoesSegmentIntersectAnyWindow(a, b))
                        {
                            destination.Add((a, b));
                        }

                        return;
                    }

                    if (!(ent is Polyline pl) || pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return;
                    }

                    for (var vi = 0; vi < pl.NumberOfVertices - 1; vi++)
                    {
                        var a = pl.GetPoint2dAt(vi);
                        var b = pl.GetPoint2dAt(vi + 1);
                        if (a.GetDistanceTo(b) <= 1e-4)
                        {
                            continue;
                        }

                        if (!IsVerticalLike(a, b) || !DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        destination.Add((a, b));
                    }
                }

                var verticalUsec = new List<(Point2d A, Point2d B)>();
                var verticalRoadBoundaries = new List<(Point2d A, Point2d B)>();
                var secHorizontalIds = new List<ObjectId>();
                var lsdVerticalIds = new List<ObjectId>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    var isLsd = string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase);
                    var isGenerated = generatedSet.Contains(id);
                    if (!isUsec && !isSec && !isLsd)
                    {
                        continue;
                    }

                    if (isUsec)
                    {
                        if (isGenerated)
                        {
                            CollectVerticalRoadSegments(ent, verticalUsec);
                        }

                        CollectVerticalRoadSegments(ent, verticalRoadBoundaries);
                        continue;
                    }

                    if (isSec)
                    {
                        CollectVerticalRoadSegments(ent, verticalRoadBoundaries);
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (isSec && horizontal && isGenerated)
                    {
                        secHorizontalIds.Add(id);
                    }
                    else if (isLsd && vertical && IsAdjustableLsdLineSegment(a, b))
                    {
                        lsdVerticalIds.Add(id);
                    }
                }

                if (verticalUsec.Count == 0 || secHorizontalIds.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine(
                        $"Cleanup: simple west-end rule skipped (verticalUsec={verticalUsec.Count}, secHoriz={secHorizontalIds.Count}).");
                    return;
                }

                const double touchTol = 0.35;
                const double minStep = 0.10;
                const double maxStep = 120.0;
                const double endpointMoveTol = 0.05;
                const double usecProjectionTol = 1.5;
                const double usecApparentYTol = 6.0;
                const double tenOhSixStep = CorrectionLinePairGapMeters;
                const double tenOhSixTol = 2.8;
                const double lsdMidpointXTol = 1.50;
                const double lsdMidpointYTol = 2.0;
                const double lsdMaxMove = 80.0;

                bool IsTouchingUsec(Point2d p)
                {
                    for (var i = 0; i < verticalUsec.Count; i++)
                    {
                        var seg = verticalUsec[i];
                        if (DistancePointToSegment(p, seg.A, seg.B) <= touchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindApparentTenOhSixTarget(
                    Point2d endpoint,
                    Vector2d outwardDir,
                    out Point2d target,
                    out double bestT)
                {
                    target = endpoint;
                    bestT = double.MaxValue;
                    if (Math.Abs(outwardDir.X) <= 1e-6)
                    {
                        return false;
                    }

                    var found = false;
                    var bestScore = double.MaxValue;
                    for (var i = 0; i < verticalUsec.Count; i++)
                    {
                        var seg = verticalUsec[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= touchTol)
                        {
                            continue;
                        }

                        var minY = Math.Min(seg.A.Y, seg.B.Y);
                        var maxY = Math.Max(seg.A.Y, seg.B.Y);
                        var yGap = 0.0;
                        if (endpoint.Y < minY)
                        {
                            yGap = minY - endpoint.Y;
                        }
                        else if (endpoint.Y > maxY)
                        {
                            yGap = endpoint.Y - maxY;
                        }

                        if (yGap > usecApparentYTol)
                        {
                            continue;
                        }

                        var axisX = 0.5 * (seg.A.X + seg.B.X);
                        var t = (axisX - endpoint.X) / outwardDir.X;
                        if (t < minStep || t > maxStep)
                        {
                            continue;
                        }

                        var score = Math.Abs(t - tenOhSixStep);
                        if (score > tenOhSixTol)
                        {
                            continue;
                        }

                        if (!found ||
                            score < (bestScore - 1e-9) ||
                            (Math.Abs(score - bestScore) <= 1e-9 && t < bestT))
                        {
                            found = true;
                            bestScore = score;
                            bestT = t;
                            target = endpoint + (outwardDir * t);
                        }
                    }

                    return found;
                }

                bool HasBlockingVerticalRoad(
                    Point2d endpoint,
                    Vector2d outwardDir,
                    double targetT)
                {
                    if (targetT <= (minStep + 0.20) || verticalRoadBoundaries.Count == 0)
                    {
                        return false;
                    }

                    var maxBlockT = targetT - 0.20;
                    for (var i = 0; i < verticalRoadBoundaries.Count; i++)
                    {
                        var seg = verticalRoadBoundaries[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= touchTol)
                        {
                            continue;
                        }

                        var hasIntersection = TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t);
                        if (!hasIntersection)
                        {
                            if (Math.Abs(outwardDir.X) <= 1e-6)
                            {
                                continue;
                            }

                            var axisX = 0.5 * (seg.A.X + seg.B.X);
                            t = (axisX - endpoint.X) / outwardDir.X;
                        }

                        if (t < minStep || t > maxBlockT)
                        {
                            continue;
                        }

                        var candidate = endpoint + (outwardDir * t);
                        if (DistancePointToSegment(candidate, seg.A, seg.B) > usecProjectionTol)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool TryFindNextUsecTarget(
                    Point2d endpoint,
                    Vector2d outwardDir,
                    out Point2d target,
                    out double bestT,
                    out bool promotedPastThirty)
                {
                    target = endpoint;
                    bestT = double.MaxValue;
                    promotedPastThirty = false;
                    if (outwardDir.Length <= 1e-6)
                    {
                        return false;
                    }

                    var candidates = new List<(double T, Point2d Target)>();
                    for (var i = 0; i < verticalUsec.Count; i++)
                    {
                        var seg = verticalUsec[i];
                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= touchTol)
                        {
                            continue;
                        }

                        var hasIntersection = TryIntersectInfiniteLineWithSegment(endpoint, outwardDir, seg.A, seg.B, out var t);
                        if (!hasIntersection)
                        {
                            if (Math.Abs(outwardDir.X) <= 1e-6)
                            {
                                continue;
                            }

                            var axisX = 0.5 * (seg.A.X + seg.B.X);
                            t = (axisX - endpoint.X) / outwardDir.X;
                        }

                        if (t < minStep || t > maxStep)
                        {
                            continue;
                        }

                        var candidate = endpoint + (outwardDir * t);
                        if (DistancePointToSegment(candidate, seg.A, seg.B) > usecProjectionTol)
                        {
                            continue;
                        }

                        candidates.Add((t, candidate));
                    }

                    if (candidates.Count == 0)
                    {
                        if (TryFindApparentTenOhSixTarget(endpoint, outwardDir, out target, out bestT))
                        {
                            if (HasBlockingVerticalRoad(endpoint, outwardDir, bestT))
                            {
                                return false;
                            }

                            return true;
                        }

                        return false;
                    }

                    candidates.Sort((l, r) => l.T.CompareTo(r.T));

                    // Prefer the inward middle-line hit (~10.06 from a 30.16 boundary) when present.
                    var selectedIndex = -1;
                    var bestTenOhSixScore = double.MaxValue;
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var score = Math.Abs(candidates[i].T - tenOhSixStep);
                        if (score > tenOhSixTol)
                        {
                            continue;
                        }

                        if (selectedIndex < 0 ||
                            score < (bestTenOhSixScore - 1e-9) ||
                            (Math.Abs(score - bestTenOhSixScore) <= 1e-9 && candidates[i].T < candidates[selectedIndex].T))
                        {
                            selectedIndex = i;
                            bestTenOhSixScore = score;
                        }
                    }

                    if (selectedIndex >= 0)
                    {
                        var selected = candidates[selectedIndex];
                        if (HasBlockingVerticalRoad(endpoint, outwardDir, selected.T))
                        {
                            return false;
                        }

                        target = selected.Target;
                        bestT = selected.T;
                        return true;
                    }

                    if (TryFindApparentTenOhSixTarget(endpoint, outwardDir, out target, out bestT))
                    {
                        if (HasBlockingVerticalRoad(endpoint, outwardDir, bestT))
                        {
                            return false;
                        }

                        return true;
                    }

                    // Strict guard: avoid west-end overshoot by only accepting 10.06-style targets.
                    return false;
                }

                var scanned = 0;
                var touching = 0;
                var candidateHits = 0;
                var promotedPastThirty = 0;
                var secAdjusted = 0;
                var secMoveInfos = new List<(Point2d OldWest, Point2d FixedEast, Point2d NewWest, Point2d OldMid, Point2d NewMid)>();
                for (var i = 0; i < secHorizontalIds.Count; i++)
                {
                    var id = secHorizontalIds[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableSec) || writableSec.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableSec, out var p0, out var p1) || !IsHorizontalLike(p0, p1))
                    {
                        continue;
                    }

                    var westIndex = p0.X <= p1.X ? 0 : 1;
                    var west = westIndex == 0 ? p0 : p1;
                    var east = westIndex == 0 ? p1 : p0;
                    scanned++;
                    if (!IsTouchingUsec(west))
                    {
                        continue;
                    }

                    touching++;
                    var outward = west - east;
                    if (outward.Length <= 1e-6)
                    {
                        continue;
                    }

                    var outwardDir = outward / outward.Length;
                    if (!TryFindNextUsecTarget(west, outwardDir, out var target, out _, out var usedTwentyBeyondThirty))
                    {
                        continue;
                    }

                    candidateHits++;
                    if (usedTwentyBeyondThirty)
                    {
                        promotedPastThirty++;
                    }

                    if (!TryMoveEndpointByIndex(writableSec, westIndex, target, endpointMoveTol))
                    {
                        continue;
                    }

                    secAdjusted++;
                    var oldMid = Midpoint(west, east);
                    var newMid = Midpoint(target, east);
                    secMoveInfos.Add((west, east, target, oldMid, newMid));
                }

                var lsdAdjusted = 0;
                if (secMoveInfos.Count > 0 && lsdVerticalIds.Count > 0)
                {
                    for (var i = 0; i < lsdVerticalIds.Count; i++)
                    {
                        var id = lsdVerticalIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1) || !IsVerticalLike(p0, p1))
                        {
                            continue;
                        }

                        var southIndex = p0.Y <= p1.Y ? 0 : 1;
                        var south = southIndex == 0 ? p0 : p1;
                        var bestFound = false;
                        var bestXDelta = double.MaxValue;
                        var bestMove = double.MaxValue;
                        var bestTarget = south;
                        for (var mi = 0; mi < secMoveInfos.Count; mi++)
                        {
                            var move = secMoveInfos[mi];
                            var xDelta = Math.Abs(south.X - move.OldMid.X);
                            if (xDelta > lsdMidpointXTol)
                            {
                                continue;
                            }

                            if (Math.Abs(south.Y - move.OldMid.Y) > lsdMidpointYTol)
                            {
                                continue;
                            }

                            var moveLen = south.GetDistanceTo(move.NewMid);
                            if (moveLen <= endpointMoveTol || moveLen > lsdMaxMove)
                            {
                                continue;
                            }

                            if (!bestFound ||
                                xDelta < (bestXDelta - 1e-9) ||
                                (Math.Abs(xDelta - bestXDelta) <= 1e-9 && moveLen < bestMove))
                            {
                                bestFound = true;
                                bestXDelta = xDelta;
                                bestMove = moveLen;
                                bestTarget = move.NewMid;
                            }
                        }

                        if (!bestFound)
                        {
                            continue;
                        }
 
                        if (TryMoveEndpointByIndex(writableLsd, southIndex, bestTarget, endpointMoveTol))
                        {
                            lsdAdjusted++;
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: simple west-end L-SEC->next L-USEC rule scanned={scanned}, touchingUsec={touching}, candidateHits={candidateHits}, promotedPastThirty={promotedPastThirty}, secAdjusted={secAdjusted}, lsdAdjusted={lsdAdjusted}.");
            }
        }

        private static void EnforceTwentyTwelveStopRules(
            Database database,
            IReadOnlyList<QuarterLabelInfo> requestedQuarterInfos,
            Logger? logger)
        {
            if (database == null || requestedQuarterInfos == null || requestedQuarterInfos.Count == 0)
            {
                return;
            }

            var requestedQuarterIds = requestedQuarterInfos
                .Where(info => info != null && !info.QuarterId.IsNull && !info.QuarterId.IsErased)
                .Select(info => info.QuarterId)
                .Distinct()
                .ToList();
            if (requestedQuarterIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryMoveEndpointByIndex(Entity writable, int endpointIndex, Point2d target, double moveTol)
                {
                    if (endpointIndex != 0 && endpointIndex != 1)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var old = endpointIndex == 0
                            ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                            : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        if (endpointIndex == 0)
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
                        var idx = endpointIndex == 0 ? 0 : pl.NumberOfVertices - 1;
                        var old = pl.GetPoint2dAt(idx);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        pl.SetPointAt(idx, target);
                        return true;
                    }

                    return false;
                }

                bool IsPointInWindow(Point2d p, Extents3d window)
                {
                    return p.X >= window.MinPoint.X &&
                           p.X <= window.MaxPoint.X &&
                           p.Y >= window.MinPoint.Y &&
                           p.Y <= window.MaxPoint.Y;
                }

                const double sectionTagBuffer = 102.0;
                var quarterContextById = new Dictionary<ObjectId, QuarterLabelInfo>();
                for (var qi = 0; qi < requestedQuarterInfos.Count; qi++)
                {
                    var info = requestedQuarterInfos[qi];
                    if (info == null || info.QuarterId.IsNull || info.QuarterId.IsErased)
                    {
                        continue;
                    }

                    if (!quarterContextById.ContainsKey(info.QuarterId))
                    {
                        quarterContextById.Add(info.QuarterId, info);
                    }
                }

                var quarterContexts = new List<(ObjectId QuarterId, Extents3d Window, Point2d Center, string SectionQuarterKeyId, string SectionQuarterLabel)>();
                foreach (var pair in quarterContextById)
                {
                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline quarter) || quarter.IsErased)
                    {
                        continue;
                    }

                    try
                    {
                        var ext = quarter.GeometricExtents;
                        var window = new Extents3d(
                            new Point3d(ext.MinPoint.X - sectionTagBuffer, ext.MinPoint.Y - sectionTagBuffer, 0.0),
                            new Point3d(ext.MaxPoint.X + sectionTagBuffer, ext.MaxPoint.Y + sectionTagBuffer, 0.0));
                        var center = new Point2d(
                            0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                            0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                        var sectionQuarterKeyId = BuildSectionQuarterKeyId(pair.Value.SectionKey, pair.Value.Quarter);
                        var sectionDescriptor = BuildSectionDescriptor(pair.Value.SectionKey);
                        var quarterToken = QuarterSelectionToToken(pair.Value.Quarter);
                        var sectionQuarterLabel = string.IsNullOrWhiteSpace(quarterToken)
                            ? sectionDescriptor
                            : $"{sectionDescriptor} {quarterToken}";
                        quarterContexts.Add((pair.Key, window, center, sectionQuarterKeyId, sectionQuarterLabel));
                    }
                    catch
                    {
                    }
                }

                (string SectionQuarterKeyId, string SectionQuarterLabel) ResolveSectionQuarter(Point2d point)
                {
                    if (quarterContexts.Count == 0)
                    {
                        return (string.Empty, "(unknown)");
                    }

                    var insideIndex = -1;
                    var insideDistance = double.MaxValue;
                    var nearestIndex = -1;
                    var nearestDistance = double.MaxValue;
                    for (var i = 0; i < quarterContexts.Count; i++)
                    {
                        var ctx = quarterContexts[i];
                        var distance = point.GetDistanceTo(ctx.Center);
                        if (IsPointInWindow(point, ctx.Window) && distance < insideDistance)
                        {
                            insideDistance = distance;
                            insideIndex = i;
                        }

                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestIndex = i;
                        }
                    }

                    var chosenIndex = insideIndex >= 0 ? insideIndex : nearestIndex;
                    if (chosenIndex < 0 || chosenIndex >= quarterContexts.Count)
                    {
                        return (string.Empty, "(unknown)");
                    }

                    var chosen = quarterContexts[chosenIndex];
                    return (chosen.SectionQuarterKeyId, chosen.SectionQuarterLabel);
                }

                var unresolvedBySectionQuarter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var unresolvedSectionLabelByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var unresolvedSamples = new List<string>();
                void RecordUnresolved(
                    ObjectId entityId,
                    string layer,
                    Point2d endpoint,
                    bool lineIsVertical,
                    Vector2d inwardDir,
                    string reason)
                {
                    var sectionInfo = ResolveSectionQuarter(endpoint);
                    var sectionKey = string.IsNullOrWhiteSpace(sectionInfo.SectionQuarterKeyId)
                        ? "UNKNOWN"
                        : sectionInfo.SectionQuarterKeyId;
                    var sectionLabel = string.IsNullOrWhiteSpace(sectionInfo.SectionQuarterLabel)
                        ? "(unknown)"
                        : sectionInfo.SectionQuarterLabel;

                    if (unresolvedBySectionQuarter.TryGetValue(sectionKey, out var count))
                    {
                        unresolvedBySectionQuarter[sectionKey] = count + 1;
                    }
                    else
                    {
                        unresolvedBySectionQuarter[sectionKey] = 1;
                    }

                    if (!unresolvedSectionLabelByKey.ContainsKey(sectionKey))
                    {
                        unresolvedSectionLabelByKey.Add(sectionKey, sectionLabel);
                    }

                    if (unresolvedSamples.Count < 16)
                    {
                        unresolvedSamples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "sec={0} id={1} layer={2} p=({3:F2},{4:F2}) orient={5} inward=({6:F4},{7:F4}) reason={8}",
                                sectionLabel,
                                entityId,
                                layer,
                                endpoint.X,
                                endpoint.Y,
                                lineIsVertical ? "V" : "H",
                                inwardDir.X,
                                inwardDir.Y,
                                reason));
                    }
                }

                var roadSegments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Axis, double MajorMin, double MajorMax, Vector2d Unit, Point2d Mid)>();
                var targetSegments = new List<(ObjectId Id, bool IsLsd, bool IsQsec)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var isRoadLayer =
                        string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isLsdLayer = string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase);
                    var isQsecLayer = string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase);
                    if (!isRoadLayer && !isLsdLayer && !isQsecLayer)
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

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    if (isRoadLayer)
                    {
                        var axis = horizontal
                            ? 0.5 * (a.Y + b.Y)
                            : 0.5 * (a.X + b.X);
                        var majorMin = horizontal
                            ? Math.Min(a.X, b.X)
                            : Math.Min(a.Y, b.Y);
                        var majorMax = horizontal
                            ? Math.Max(a.X, b.X)
                            : Math.Max(a.Y, b.Y);
                        var dir = b - a;
                        var dirLen = dir.Length;
                        if (dirLen <= 1e-6)
                        {
                            continue;
                        }

                        var unit = dir / dirLen;
                        var mid = Midpoint(a, b);
                        roadSegments.Add((id, layer, a, b, horizontal, vertical, axis, majorMin, majorMax, unit, mid));
                        continue;
                    }

                    if (isLsdLayer && !IsAdjustableLsdLineSegment(a, b))
                    {
                        continue;
                    }

                    if (isQsecLayer)
                    {
                        var len = a.GetDistanceTo(b);
                        if (len < 8.0 || len > 2000.0)
                        {
                            continue;
                        }
                    }

                    targetSegments.Add((id, isLsdLayer, isQsecLayer));
                }

                if (roadSegments.Count == 0 || targetSegments.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine(
                        $"Cleanup: 20.11 stop rules skipped (roads={roadSegments.Count}, targets={targetSegments.Count}).");
                    return;
                }

                const double twentyTwelveGap = 20.11;
                const double thirtyEighteenGap = RoadAllowanceUsecWidthMeters;
                const double tenOhSixGap = CorrectionLinePairGapMeters;
                const double twentyGapTol = 1.10;
                const double thirtyGapTol = 1.60;
                const double tenOhSixTol = 2.40;
                const double companionMinOverlap = 4.0;
                const double fallbackMinOverlap = 2.0;
                var hasTwentyCompanion = new bool[roadSegments.Count];
                var hasThirtyCompanion = new bool[roadSegments.Count];
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
                        if (overlap < companionMinOverlap)
                        {
                            continue;
                        }

                        var gap = Math.Abs(a.Axis - b.Axis);
                        if (Math.Abs(gap - twentyTwelveGap) <= twentyGapTol)
                        {
                            hasTwentyCompanion[i] = true;
                            hasTwentyCompanion[j] = true;
                        }

                        if (Math.Abs(gap - thirtyEighteenGap) <= thirtyGapTol)
                        {
                            hasThirtyCompanion[i] = true;
                            hasThirtyCompanion[j] = true;
                        }
                    }
                }

                var twentyRoadIndices = new List<int>();
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    if (hasTwentyCompanion[i])
                    {
                        twentyRoadIndices.Add(i);
                    }
                }

                const double endpointTouchTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double minInwardStep = 0.05;
                const double maxInwardSearch = 28.0;
                const double inwardLengthSlack = 0.90;
                const double axisFallbackMajorTol = 22.0;
                const double lsdMidpointAxisTol = 2.0;
                const double lsdMaxMove = 24.0;
                const double qsecMaxMove = 24.0;
                const double localTwentyGapTol = 1.90;
                const double localTwentyGapTolRelaxed = 2.80;
                const double localTenOhSixGapTol = 2.80;
                const double localPairMinOverlap = 0.75;

                var thirtyOnlyRoadIndices = new List<int>();
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    if (hasThirtyCompanion[i] && !hasTwentyCompanion[i])
                    {
                        thirtyOnlyRoadIndices.Add(i);
                    }
                }

                List<int> GetTouchedRoadIndices(Point2d endpoint)
                {
                    var touchedIndices = new List<int>();
                    for (var ri = 0; ri < roadSegments.Count; ri++)
                    {
                        var road = roadSegments[ri];
                        if (DistancePointToSegment(endpoint, road.A, road.B) <= endpointTouchTol)
                        {
                            touchedIndices.Add(ri);
                        }
                    }

                    return touchedIndices;
                }

                bool TryFindNearestInwardTarget(
                    Point2d endpoint,
                    Vector2d inwardDir,
                    bool lineIsVertical,
                    double maxT,
                    IEnumerable<int> candidateIndices,
                    out Point2d target,
                    out int targetRoadIndex,
                    out double bestT)
                {
                    target = endpoint;
                    targetRoadIndex = -1;
                    bestT = double.MaxValue;
                    var found = false;
                    foreach (var ri in candidateIndices)
                    {
                        if (ri < 0 || ri >= roadSegments.Count)
                        {
                            continue;
                        }

                        var road = roadSegments[ri];
                        if (lineIsVertical && !road.Horizontal)
                        {
                            continue;
                        }

                        if (!lineIsVertical && !road.Vertical)
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLineWithSegment(endpoint, inwardDir, road.A, road.B, out var t))
                        {
                            if (lineIsVertical)
                            {
                                if (Math.Abs(inwardDir.Y) <= 1e-9)
                                {
                                    continue;
                                }

                                t = (road.Axis - endpoint.Y) / inwardDir.Y;
                                if (t < minInwardStep || t > maxT)
                                {
                                    continue;
                                }

                                var xAtT = endpoint.X + (inwardDir.X * t);
                                var majorGap = 0.0;
                                if (xAtT < road.MajorMin)
                                {
                                    majorGap = road.MajorMin - xAtT;
                                }
                                else if (xAtT > road.MajorMax)
                                {
                                    majorGap = xAtT - road.MajorMax;
                                }

                                if (majorGap > axisFallbackMajorTol)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                if (Math.Abs(inwardDir.X) <= 1e-9)
                                {
                                    continue;
                                }

                                t = (road.Axis - endpoint.X) / inwardDir.X;
                                if (t < minInwardStep || t > maxT)
                                {
                                    continue;
                                }

                                var yAtT = endpoint.Y + (inwardDir.Y * t);
                                var majorGap = 0.0;
                                if (yAtT < road.MajorMin)
                                {
                                    majorGap = road.MajorMin - yAtT;
                                }
                                else if (yAtT > road.MajorMax)
                                {
                                    majorGap = yAtT - road.MajorMax;
                                }

                                if (majorGap > axisFallbackMajorTol)
                                {
                                    continue;
                                }
                            }
                        }

                        if (t < minInwardStep || t > maxT)
                        {
                            continue;
                        }

                        if (!found || t < (bestT - 1e-9))
                        {
                            found = true;
                            bestT = t;
                            targetRoadIndex = ri;
                            target = endpoint + (inwardDir * t);
                        }
                    }

                    return found;
                }

                bool TryFindSectionLocalTwentyProjection(
                    Point2d endpoint,
                    Vector2d inwardDir,
                    bool lineIsVertical,
                    IReadOnlyList<int> touchedThirtyOnly,
                    out Point2d target,
                    out int outerRoadIndex,
                    out int twentyRoadIndex,
                    out bool usedClampedExtent)
                {
                    target = endpoint;
                    outerRoadIndex = -1;
                    twentyRoadIndex = -1;
                    usedClampedExtent = false;

                    var outerCandidates = touchedThirtyOnly != null && touchedThirtyOnly.Count > 0
                        ? touchedThirtyOnly
                        : thirtyOnlyRoadIndices;
                    if (outerCandidates == null || outerCandidates.Count == 0)
                    {
                        return false;
                    }

                    bool TryFindLocalPass(
                        bool requireSameLayer,
                        double minOverlap,
                        double gapTol,
                        out Point2d passTarget,
                        out int passOuterRoadIndex,
                        out int passTwentyRoadIndex,
                        out bool passUsedClampedExtent)
                    {
                        passTarget = endpoint;
                        passOuterRoadIndex = -1;
                        passTwentyRoadIndex = -1;
                        passUsedClampedExtent = false;

                        var found = false;
                        var bestScore = double.MaxValue;
                        for (var oi = 0; oi < outerCandidates.Count; oi++)
                        {
                            var outerIndex = outerCandidates[oi];
                            if (outerIndex < 0 || outerIndex >= roadSegments.Count)
                            {
                                continue;
                            }

                            var outer = roadSegments[outerIndex];
                            if (lineIsVertical && !outer.Horizontal)
                            {
                                continue;
                            }

                            if (!lineIsVertical && !outer.Vertical)
                            {
                                continue;
                            }

                            for (var ri = 0; ri < roadSegments.Count; ri++)
                            {
                                if (ri == outerIndex)
                                {
                                    continue;
                                }

                                var inner = roadSegments[ri];
                                if ((inner.Horizontal != outer.Horizontal) || (inner.Vertical != outer.Vertical))
                                {
                                    continue;
                                }

                                var sameLayer = string.Equals(inner.Layer, outer.Layer, StringComparison.OrdinalIgnoreCase);
                                if (requireSameLayer && !sameLayer)
                                {
                                    continue;
                                }

                                var overlap = Math.Min(outer.MajorMax, inner.MajorMax) - Math.Max(outer.MajorMin, inner.MajorMin);
                                if (overlap < minOverlap)
                                {
                                    continue;
                                }

                                var gap = Math.Abs(inner.Axis - outer.Axis);
                                var gapDeltaTwenty = Math.Abs(gap - twentyTwelveGap);
                                var gapDeltaTenOhSix = Math.Abs(gap - tenOhSixGap);
                                if (gapDeltaTwenty > gapTol && gapDeltaTenOhSix > localTenOhSixGapTol)
                                {
                                    continue;
                                }
                                var gapDelta = Math.Min(gapDeltaTwenty, gapDeltaTenOhSix);

                                var blended = outer.Unit + inner.Unit;
                                var blendedLen = blended.Length;
                                var along = blendedLen > 1e-6 ? (blended / blendedLen) : outer.Unit;
                                if (along.Length <= 1e-6)
                                {
                                    continue;
                                }

                                var normal = new Vector2d(-along.Y, along.X);
                                if (normal.DotProduct(inwardDir) < 0.0)
                                {
                                    normal = -normal;
                                }

                                var normalOffset = (inner.Mid - endpoint).DotProduct(normal);
                                if (normalOffset < minInwardStep || normalOffset > maxInwardSearch)
                                {
                                    continue;
                                }
                                var normalOffsetDeltaTwenty = Math.Abs(normalOffset - twentyTwelveGap);
                                var normalOffsetDeltaTenOhSix = Math.Abs(normalOffset - tenOhSixGap);
                                var normalOffsetDelta = Math.Min(normalOffsetDeltaTwenty, normalOffsetDeltaTenOhSix);
                                if (normalOffsetDelta > Math.Max(gapTol, localTenOhSixGapTol))
                                {
                                    continue;
                                }

                                var uA = (inner.A - endpoint).DotProduct(along);
                                var uB = (inner.B - endpoint).DotProduct(along);
                                var uMin = Math.Min(uA, uB);
                                var uMax = Math.Max(uA, uB);

                                var projectedU = 0.0;
                                var clampUsed = false;
                                if (projectedU < uMin)
                                {
                                    projectedU = uMin;
                                    clampUsed = true;
                                }
                                else if (projectedU > uMax)
                                {
                                    projectedU = uMax;
                                    clampUsed = true;
                                }

                                var majorGap = clampUsed ? Math.Abs(projectedU) : 0.0;
                                if (majorGap > axisFallbackMajorTol)
                                {
                                    continue;
                                }

                                var candidateTarget = endpoint + (along * projectedU) + (normal * normalOffset);
                                var score =
                                    DistancePointToSegment(endpoint, outer.A, outer.B) +
                                    normalOffsetDelta +
                                    (0.15 * majorGap) +
                                    (0.10 * gapDelta) +
                                    (sameLayer ? 0.0 : 0.20) +
                                    (overlap < localPairMinOverlap ? 0.12 : 0.0);
                                if (!found || score < (bestScore - 1e-9))
                                {
                                    found = true;
                                    bestScore = score;
                                    passTarget = candidateTarget;
                                    passOuterRoadIndex = outerIndex;
                                    passTwentyRoadIndex = ri;
                                    passUsedClampedExtent = clampUsed;
                                }
                            }
                        }

                        return found;
                    }

                    if (TryFindLocalPass(
                            requireSameLayer: true,
                            minOverlap: localPairMinOverlap,
                            gapTol: localTwentyGapTol,
                            out var strictTarget,
                            out var strictOuter,
                            out var strictTwenty,
                            out var strictClamp))
                    {
                        target = strictTarget;
                        outerRoadIndex = strictOuter;
                        twentyRoadIndex = strictTwenty;
                        usedClampedExtent = strictClamp;
                        return true;
                    }

                    if (TryFindLocalPass(
                            requireSameLayer: false,
                            minOverlap: 0.0,
                            gapTol: localTwentyGapTolRelaxed,
                            out var relaxedTarget,
                            out var relaxedOuter,
                            out var relaxedTwenty,
                            out var relaxedClamp))
                    {
                        target = relaxedTarget;
                        outerRoadIndex = relaxedOuter;
                        twentyRoadIndex = relaxedTwenty;
                        usedClampedExtent = relaxedClamp;
                        return true;
                    }

                    return false;
                }

                var scannedEndpoints = 0;
                var touchedRoadEndpoints = 0;
                var touchedThirtyOnlyEndpoints = 0;
                var noTwentyTarget = 0;
                var lsdUnresolvedBeforeHardStop = 0;
                var adjustedLsd = 0;
                var adjustedQsec = 0;
                var midpointMoves = 0;
                var fallbackMoves = 0;
                var fallbackReverseMoves = 0;
                var localBasisMoves = 0;
                var localBasisClampMoves = 0;
                var localBasisReverseMoves = 0;
                for (var ti = 0; ti < targetSegments.Count; ti++)
                {
                    var targetMeta = targetSegments[ti];
                    if (!(tr.GetObject(targetMeta.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var targetLayer = writable.Layer ?? string.Empty;
                    var lineIsHorizontal = IsHorizontalLike(p0, p1);
                    var lineIsVertical = IsVerticalLike(p0, p1);
                    if (!lineIsHorizontal && !lineIsVertical)
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        scannedEndpoints++;
                        var endpoint = endpointIndex == 0 ? p0 : p1;
                        var other = endpointIndex == 0 ? p1 : p0;
                        var inward = other - endpoint;
                        var inwardLen = inward.Length;
                        if (inwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var inwardDir = inward / inwardLen;
                        var touchedIndices = GetTouchedRoadIndices(endpoint);

                        if (touchedIndices.Count == 0)
                        {
                            continue;
                        }

                        touchedRoadEndpoints++;
                        var onTwenty = touchedIndices.Any(ri => hasTwentyCompanion[ri]);
                        if (onTwenty)
                        {
                            continue;
                        }

                        var touchedThirtyOnly = touchedIndices
                            .Where(ri => hasThirtyCompanion[ri] && !hasTwentyCompanion[ri])
                            .ToList();
                        if (touchedThirtyOnly.Count == 0)
                        {
                            continue;
                        }

                        touchedThirtyOnlyEndpoints++;
                        var maxT = Math.Min(maxInwardSearch, inwardLen + inwardLengthSlack);
                        var hasTarget = TryFindNearestInwardTarget(
                            endpoint,
                            inwardDir,
                            lineIsVertical,
                            maxT,
                            twentyRoadIndices,
                            out var bestTarget,
                            out var bestRoadIndex,
                            out _);
                        var usedFallback = false;
                        var usedFallbackReverse = false;
                        var usedLocalBasis = false;
                        var usedLocalClamp = false;
                        var usedLocalReverse = false;
                        if (!hasTarget)
                        {
                            var fallbackCandidates = new HashSet<int>();
                            for (var oi = 0; oi < touchedThirtyOnly.Count; oi++)
                            {
                                var outerIndex = touchedThirtyOnly[oi];
                                var outer = roadSegments[outerIndex];
                                for (var ri = 0; ri < roadSegments.Count; ri++)
                                {
                                    if (ri == outerIndex)
                                    {
                                        continue;
                                    }

                                    var road = roadSegments[ri];
                                    if ((road.Horizontal != outer.Horizontal) || (road.Vertical != outer.Vertical))
                                    {
                                        continue;
                                    }

                                    if (!string.Equals(road.Layer, outer.Layer, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    var overlap = Math.Min(outer.MajorMax, road.MajorMax) - Math.Max(outer.MajorMin, road.MajorMin);
                                    if (overlap < fallbackMinOverlap)
                                    {
                                        continue;
                                    }

                                    var gap = Math.Abs(road.Axis - outer.Axis);
                                    if (Math.Abs(gap - tenOhSixGap) > tenOhSixTol &&
                                        Math.Abs(gap - twentyTwelveGap) > twentyGapTol)
                                    {
                                        continue;
                                    }

                                    fallbackCandidates.Add(ri);
                                }
                            }

                            if (fallbackCandidates.Count > 0)
                            {
                                hasTarget = TryFindNearestInwardTarget(
                                    endpoint,
                                    inwardDir,
                                    lineIsVertical,
                                    maxT,
                                    fallbackCandidates,
                                    out bestTarget,
                                    out bestRoadIndex,
                                    out _);
                                if (!hasTarget)
                                {
                                    hasTarget = TryFindNearestInwardTarget(
                                        endpoint,
                                        -inwardDir,
                                        lineIsVertical,
                                        maxT,
                                        fallbackCandidates,
                                        out bestTarget,
                                        out bestRoadIndex,
                                        out _);
                                    usedFallbackReverse = hasTarget;
                                }

                                usedFallback = hasTarget;
                            }
                        }

                        if (!hasTarget && targetMeta.IsLsd)
                        {
                            hasTarget = TryFindSectionLocalTwentyProjection(
                                endpoint,
                                inwardDir,
                                lineIsVertical,
                                touchedThirtyOnly,
                                out bestTarget,
                                out _,
                                out bestRoadIndex,
                                out usedLocalClamp);
                            if (!hasTarget)
                            {
                                hasTarget = TryFindSectionLocalTwentyProjection(
                                    endpoint,
                                    -inwardDir,
                                    lineIsVertical,
                                    touchedThirtyOnly,
                                    out bestTarget,
                                    out _,
                                    out bestRoadIndex,
                                    out usedLocalClamp);
                                usedLocalReverse = hasTarget;
                            }

                            usedLocalBasis = hasTarget;
                        }

                        if (!hasTarget || bestRoadIndex < 0)
                        {
                            if (targetMeta.IsLsd)
                            {
                                lsdUnresolvedBeforeHardStop++;
                            }
                            else
                            {
                                noTwentyTarget++;
                                RecordUnresolved(
                                    targetMeta.Id,
                                    targetLayer,
                                    endpoint,
                                    lineIsVertical,
                                    inwardDir,
                                    "no-20.11-target");
                            }

                            continue;
                        }

                        var moveTarget = bestTarget;
                        var usedMidpoint = false;
                        if (targetMeta.IsLsd && !usedLocalBasis)
                        {
                            var road = roadSegments[bestRoadIndex];
                            var mid = Midpoint(road.A, road.B);
                            if (lineIsVertical)
                            {
                                if (Math.Abs(mid.X - endpoint.X) <= lsdMidpointAxisTol)
                                {
                                    moveTarget = mid;
                                    usedMidpoint = true;
                                }
                            }
                            else if (lineIsHorizontal)
                            {
                                if (Math.Abs(mid.Y - endpoint.Y) <= lsdMidpointAxisTol)
                                {
                                    moveTarget = mid;
                                    usedMidpoint = true;
                                }
                            }
                        }

                        var moveLen = endpoint.GetDistanceTo(moveTarget);
                        var moveCap = targetMeta.IsLsd ? lsdMaxMove : qsecMaxMove;
                        if (moveLen <= endpointMoveTol || moveLen > moveCap)
                        {
                            continue;
                        }

                        if (!TryMoveEndpointByIndex(writable, endpointIndex, moveTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        if (targetMeta.IsLsd)
                        {
                            adjustedLsd++;
                        }
                        else if (targetMeta.IsQsec)
                        {
                            adjustedQsec++;
                        }

                        if (usedMidpoint)
                        {
                            midpointMoves++;
                        }

                        if (usedFallback)
                        {
                            fallbackMoves++;
                            if (usedFallbackReverse)
                            {
                                fallbackReverseMoves++;
                            }
                        }

                        if (usedLocalBasis)
                        {
                            localBasisMoves++;
                            if (usedLocalClamp)
                            {
                                localBasisClampMoves++;
                            }

                            if (usedLocalReverse)
                            {
                                localBasisReverseMoves++;
                            }
                        }

                        if (endpointIndex == 0)
                        {
                            p0 = moveTarget;
                        }
                        else
                        {
                            p1 = moveTarget;
                        }
                    }
                }

                var hardStopScanned = 0;
                var hardStopAdjusted = 0;
                var hardStopClampMoves = 0;
                var hardStopReverseMoves = 0;
                var hardStopUnresolved = 0;
                for (var ti = 0; ti < targetSegments.Count; ti++)
                {
                    var targetMeta = targetSegments[ti];
                    if (!targetMeta.IsLsd)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(targetMeta.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var targetLayer = writable.Layer ?? string.Empty;
                    var lineIsHorizontal = IsHorizontalLike(p0, p1);
                    var lineIsVertical = IsVerticalLike(p0, p1);
                    if (!lineIsHorizontal && !lineIsVertical)
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? p0 : p1;
                        var other = endpointIndex == 0 ? p1 : p0;
                        var touchedIndices = GetTouchedRoadIndices(endpoint);
                        if (touchedIndices.Count == 0)
                        {
                            continue;
                        }

                        if (touchedIndices.Any(ri => hasTwentyCompanion[ri]))
                        {
                            continue;
                        }

                        var touchedThirtyOnly = touchedIndices
                            .Where(ri => hasThirtyCompanion[ri] && !hasTwentyCompanion[ri])
                            .ToList();
                        if (touchedThirtyOnly.Count == 0)
                        {
                            continue;
                        }

                        hardStopScanned++;
                        var inward = other - endpoint;
                        var inwardLen = inward.Length;
                        if (inwardLen <= 1e-6)
                        {
                            hardStopUnresolved++;
                            noTwentyTarget++;
                            RecordUnresolved(
                                targetMeta.Id,
                                targetLayer,
                                endpoint,
                                lineIsVertical,
                                new Vector2d(0.0, 0.0),
                                "hard-stop-zero-inward");
                            continue;
                        }

                        var inwardDir = inward / inwardLen;
                        var hardStopUsedReverse = false;
                        if (!TryFindSectionLocalTwentyProjection(
                                endpoint,
                                inwardDir,
                                lineIsVertical,
                                touchedThirtyOnly,
                                out var hardStopTarget,
                                out _,
                                out _,
                                out var hardStopClamp))
                        {
                            if (!TryFindSectionLocalTwentyProjection(
                                    endpoint,
                                    -inwardDir,
                                    lineIsVertical,
                                    touchedThirtyOnly,
                                    out hardStopTarget,
                                    out _,
                                    out _,
                                    out hardStopClamp))
                            {
                                hardStopUnresolved++;
                                noTwentyTarget++;
                                RecordUnresolved(
                                    targetMeta.Id,
                                    targetLayer,
                                    endpoint,
                                    lineIsVertical,
                                    inwardDir,
                                    "hard-stop-no-local-axis");
                                continue;
                            }

                            hardStopUsedReverse = true;
                        }

                        var moveLen = endpoint.GetDistanceTo(hardStopTarget);
                        if (moveLen <= endpointMoveTol || moveLen > lsdMaxMove)
                        {
                            hardStopUnresolved++;
                            noTwentyTarget++;
                            RecordUnresolved(
                                targetMeta.Id,
                                targetLayer,
                                endpoint,
                                lineIsVertical,
                                inwardDir,
                                "hard-stop-move-out-of-range");
                            continue;
                        }

                        if (!TryMoveEndpointByIndex(writable, endpointIndex, hardStopTarget, endpointMoveTol))
                        {
                            hardStopUnresolved++;
                            noTwentyTarget++;
                            RecordUnresolved(
                                targetMeta.Id,
                                targetLayer,
                                endpoint,
                                lineIsVertical,
                                inwardDir,
                                "hard-stop-move-failed");
                            continue;
                        }

                        hardStopAdjusted++;
                        adjustedLsd++;
                        if (hardStopClamp)
                        {
                            hardStopClampMoves++;
                        }
                        if (hardStopUsedReverse)
                        {
                            hardStopReverseMoves++;
                        }

                        if (endpointIndex == 0)
                        {
                            p0 = hardStopTarget;
                        }
                        else
                        {
                            p1 = hardStopTarget;
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: enforced 20.11 stop rules scanned={scannedEndpoints}, touchedRoad={touchedRoadEndpoints}, touched30Only={touchedThirtyOnlyEndpoints}, adjustedLsd={adjustedLsd}, adjustedQsec={adjustedQsec}, midpointMoves={midpointMoves}, fallbackMoves={fallbackMoves}, fallbackReverseMoves={fallbackReverseMoves}, localBasisMoves={localBasisMoves}, localClampMoves={localBasisClampMoves}, localReverseMoves={localBasisReverseMoves}, preHardStopLsdUnresolved={lsdUnresolvedBeforeHardStop}, hardStopScanned={hardStopScanned}, hardStopAdjusted={hardStopAdjusted}, hardStopClampMoves={hardStopClampMoves}, hardStopReverseMoves={hardStopReverseMoves}, hardStopUnresolved={hardStopUnresolved}, unresolved={noTwentyTarget}.");
                if (noTwentyTarget > 0)
                {
                    logger?.WriteLine(
                        $"RULE-WARN: {noTwentyTarget} endpoint(s) touched 30.16 boundary but no valid inward 20.11 target was found after hard-stop.");
                    if (unresolvedSamples.Count > 0)
                    {
                        logger?.WriteLine("RULE-WARN samples: " + string.Join(" | ", unresolvedSamples));
                    }

                    if (unresolvedBySectionQuarter.Count > 0)
                    {
                        var sectionRows = unresolvedBySectionQuarter
                            .OrderByDescending(pair => pair.Value)
                            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(pair =>
                            {
                                var sectionLabel = unresolvedSectionLabelByKey.TryGetValue(pair.Key, out var label)
                                    ? label
                                    : pair.Key;
                                return $"{sectionLabel} ({pair.Key})={pair.Value}";
                            })
                            .ToList();
                        logger?.WriteLine("RULE-WARN unresolved by section-quarter: " + string.Join(" | ", sectionRows));
                    }
                }
            }
        }

        private static void ForceInwardTenOhSixFromOuterUsec(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryMoveEndpointByIndex(Entity writable, int endpointIndex, Point2d target, double moveTol)
                {
                    if (endpointIndex != 0 && endpointIndex != 1)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var old = endpointIndex == 0
                            ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                            : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        if (endpointIndex == 0)
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
                        var idx = endpointIndex == 0 ? 0 : pl.NumberOfVertices - 1;
                        var old = pl.GetPoint2dAt(idx);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        pl.SetPointAt(idx, target);
                        return true;
                    }

                    return false;
                }

                var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var verticalRoads = new List<(Point2d A, Point2d B, bool Generated)>();
                var horizontalRoads = new List<(Point2d A, Point2d B, bool Generated)>();
                var secMovables = new List<ObjectId>();
                var lsdHorizontalMovables = new List<ObjectId>();
                var lsdVerticalMovables = new List<ObjectId>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    var isLsd = string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec && !isLsd)
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

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if ((isUsec || isSec) && vertical)
                    {
                        verticalRoads.Add((a, b, generatedSet.Contains(id)));
                    }

                    if ((isUsec || isSec) && horizontal)
                    {
                        horizontalRoads.Add((a, b, generatedSet.Contains(id)));
                    }

                    if (isSec && horizontal)
                    {
                        secMovables.Add(id);
                    }
                    else if (isLsd && IsAdjustableLsdLineSegment(a, b))
                    {
                        if (horizontal)
                        {
                            lsdHorizontalMovables.Add(id);
                        }
                        else if (vertical)
                        {
                            lsdVerticalMovables.Add(id);
                        }
                    }
                }

                if (verticalRoads.Count == 0 &&
                    horizontalRoads.Count == 0 &&
                    secMovables.Count == 0 &&
                    lsdHorizontalMovables.Count == 0 &&
                    lsdVerticalMovables.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine(
                        $"Cleanup: forced inward 10.06 skipped (verticalRoads={verticalRoads.Count}, horizontalRoads={horizontalRoads.Count}, secMovables={secMovables.Count}, lsdH={lsdHorizontalMovables.Count}, lsdV={lsdVerticalMovables.Count}).");
                    return;
                }

                const double touchTol = 0.35;
                const double outerCompanionTarget = RoadAllowanceUsecWidthMeters;
                const double outerCompanionTol = 3.0;
                const double targetCompanionTarget = 20.11;
                const double targetCompanionTol = 2.70;
                const double companionMinOverlap = 10.0;
                const double stepTargetPrimary = CorrectionLinePairGapMeters;
                const double stepTargetSecondary = 20.11;
                const double stepMin = 0.10;
                const double stepMax = 45.0;
                const double endpointMoveTol = 0.05;
                const double lsdMaxMove = 16.50;
                var scanned = 0;
                var touchingOuter = 0;
                var touchingOuterThirtyEighteen = 0;
                var candidates = 0;
                var adjusted = 0;
                var lsdAdjusted = 0;
                var outerCompanionCache = new Dictionary<int, bool>();
                var targetCompanionCache = new Dictionary<int, bool>();
                var outerHorizontalCompanionCache = new Dictionary<int, bool>();
                var targetHorizontalCompanionCache = new Dictionary<int, bool>();

                double AxisX((Point2d A, Point2d B, bool Generated) seg)
                {
                    return 0.5 * (seg.A.X + seg.B.X);
                }

                double OverlapY((Point2d A, Point2d B, bool Generated) a, (Point2d A, Point2d B, bool Generated) b)
                {
                    var aMin = Math.Min(a.A.Y, a.B.Y);
                    var aMax = Math.Max(a.A.Y, a.B.Y);
                    var bMin = Math.Min(b.A.Y, b.B.Y);
                    var bMax = Math.Max(b.A.Y, b.B.Y);
                    return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                }

                bool ComputeHasCompanionAtOffset(
                    int index,
                    double expectedOffset,
                    double tol,
                    Dictionary<int, bool> cache)
                {
                    if (cache.TryGetValue(index, out var cached))
                    {
                        return cached;
                    }

                    if (index < 0 || index >= verticalRoads.Count)
                    {
                        cache[index] = false;
                        return false;
                    }

                    var s = verticalRoads[index];
                    var sAxis = AxisX(s);
                    var found = false;
                    for (var oi = 0; oi < verticalRoads.Count; oi++)
                    {
                        if (oi == index)
                        {
                            continue;
                        }

                        var o = verticalRoads[oi];
                        if (OverlapY(s, o) < companionMinOverlap)
                        {
                            continue;
                        }

                        var offset = Math.Abs(AxisX(o) - sAxis);
                        if (Math.Abs(offset - expectedOffset) <= tol)
                        {
                            found = true;
                            break;
                        }
                    }

                    cache[index] = found;
                    return found;
                }

                bool HasOuterCompanion(int index)
                {
                    return ComputeHasCompanionAtOffset(
                        index,
                        outerCompanionTarget,
                        outerCompanionTol,
                        outerCompanionCache);
                }

                bool HasTargetCompanion(int index)
                {
                    return ComputeHasCompanionAtOffset(
                        index,
                        targetCompanionTarget,
                        targetCompanionTol,
                        targetCompanionCache);
                }

                double AxisY((Point2d A, Point2d B, bool Generated) seg)
                {
                    return 0.5 * (seg.A.Y + seg.B.Y);
                }

                double OverlapX((Point2d A, Point2d B, bool Generated) a, (Point2d A, Point2d B, bool Generated) b)
                {
                    var aMin = Math.Min(a.A.X, a.B.X);
                    var aMax = Math.Max(a.A.X, a.B.X);
                    var bMin = Math.Min(b.A.X, b.B.X);
                    var bMax = Math.Max(b.A.X, b.B.X);
                    return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                }

                bool ComputeHorizontalHasCompanionAtOffset(
                    int index,
                    double expectedOffset,
                    double tol,
                    Dictionary<int, bool> cache)
                {
                    if (cache.TryGetValue(index, out var cached))
                    {
                        return cached;
                    }

                    if (index < 0 || index >= horizontalRoads.Count)
                    {
                        cache[index] = false;
                        return false;
                    }

                    var s = horizontalRoads[index];
                    var sAxis = AxisY(s);
                    var found = false;
                    for (var oi = 0; oi < horizontalRoads.Count; oi++)
                    {
                        if (oi == index)
                        {
                            continue;
                        }

                        var o = horizontalRoads[oi];
                        if (OverlapX(s, o) < companionMinOverlap)
                        {
                            continue;
                        }

                        var offset = Math.Abs(AxisY(o) - sAxis);
                        if (Math.Abs(offset - expectedOffset) <= tol)
                        {
                            found = true;
                            break;
                        }
                    }

                    cache[index] = found;
                    return found;
                }

                bool HasOuterHorizontalCompanion(int index)
                {
                    return ComputeHorizontalHasCompanionAtOffset(
                        index,
                        outerCompanionTarget,
                        outerCompanionTol,
                        outerHorizontalCompanionCache);
                }

                bool HasTargetHorizontalCompanion(int index)
                {
                    return ComputeHorizontalHasCompanionAtOffset(
                        index,
                        targetCompanionTarget,
                        targetCompanionTol,
                        targetHorizontalCompanionCache);
                }

                bool TryFindBestTarget(
                    Point2d endpoint,
                    Vector2d inwardDir,
                    out Point2d bestTarget,
                    out double bestScore,
                    out double bestT,
                    out bool endpointTouchedOuter,
                    out bool endpointTouchedOuterThirtyEighteen)
                {
                    bestTarget = endpoint;
                    bestScore = double.MaxValue;
                    bestT = double.MaxValue;
                    endpointTouchedOuter = false;
                    endpointTouchedOuterThirtyEighteen = false;
                    var found = false;

                    for (var oi = 0; oi < verticalRoads.Count; oi++)
                    {
                        var outer = verticalRoads[oi];
                        if (DistancePointToSegment(endpoint, outer.A, outer.B) > touchTol)
                        {
                            continue;
                        }

                        endpointTouchedOuter = true;
                        if (!HasOuterCompanion(oi))
                        {
                            continue;
                        }

                        endpointTouchedOuterThirtyEighteen = true;
                        if (Math.Abs(inwardDir.X) <= 1e-3)
                        {
                            continue;
                        }

                        var outerAxisX = AxisX(outer);
                        var inwardSign = Math.Sign(inwardDir.X);
                        for (var pass = 0; pass <= 1; pass++)
                        {
                            for (var ti = 0; ti < verticalRoads.Count; ti++)
                            {
                                if (ti == oi)
                                {
                                    continue;
                                }

                                var targetHasTwentyCompanion = HasTargetCompanion(ti);
                                var targetHasThirtyCompanion = HasOuterCompanion(ti);
                                if (pass == 0 && !targetHasTwentyCompanion)
                                {
                                    continue;
                                }

                                if (pass == 1)
                                {
                                    if (targetHasTwentyCompanion)
                                    {
                                        continue;
                                    }

                                    if (targetHasThirtyCompanion)
                                    {
                                        continue;
                                    }
                                }

                                var targetSeg = verticalRoads[ti];
                                var targetAxisX = AxisX(targetSeg);
                                var t = (targetAxisX - outerAxisX) * inwardSign;
                                if (t < stepMin || t > stepMax)
                                {
                                    continue;
                                }

                                var score =
                                    (pass * 1000.0) +
                                    0.65 * Math.Min(
                                        Math.Abs(t - stepTargetPrimary),
                                        Math.Abs(t - stepTargetSecondary)) +
                                    0.35 * t;
                                if (targetHasTwentyCompanion)
                                {
                                    score -= 0.08;
                                }

                                if (targetSeg.Generated)
                                {
                                    score -= 0.03;
                                }

                                if (!found ||
                                    score < (bestScore - 1e-9) ||
                                    (Math.Abs(score - bestScore) <= 1e-9 && t < bestT))
                                {
                                    found = true;
                                    bestScore = score;
                                    bestT = t;
                                    bestTarget = new Point2d(targetAxisX, endpoint.Y);
                                }
                            }
                        }
                    }

                    return found;
                }

                bool TryFindBestVerticalLsdTarget(
                    Point2d southEndpoint,
                    Point2d northEndpoint,
                    out Point2d bestTarget)
                {
                    bestTarget = southEndpoint;
                    if (horizontalRoads.Count == 0)
                    {
                        return false;
                    }

                    var dirY = northEndpoint.Y - southEndpoint.Y;
                    if (Math.Abs(dirY) <= 1e-6)
                    {
                        return false;
                    }

                    var inwardSign = Math.Sign(dirY);
                    var found = false;
                    var bestScore = double.MaxValue;
                    var bestT = double.MaxValue;
                    for (var oi = 0; oi < horizontalRoads.Count; oi++)
                    {
                        var outer = horizontalRoads[oi];
                        if (DistancePointToSegment(southEndpoint, outer.A, outer.B) > touchTol)
                        {
                            continue;
                        }

                        if (!HasOuterHorizontalCompanion(oi))
                        {
                            continue;
                        }

                        var outerAxisY = AxisY(outer);
                        for (var pass = 0; pass <= 1; pass++)
                        {
                            for (var ti = 0; ti < horizontalRoads.Count; ti++)
                            {
                                if (ti == oi)
                                {
                                    continue;
                                }

                                var targetHasTwentyCompanion = HasTargetHorizontalCompanion(ti);
                                var targetHasThirtyCompanion = HasOuterHorizontalCompanion(ti);
                                if (pass == 0 && !targetHasTwentyCompanion)
                                {
                                    continue;
                                }

                                if (pass == 1)
                                {
                                    if (targetHasTwentyCompanion)
                                    {
                                        continue;
                                    }

                                    if (targetHasThirtyCompanion)
                                    {
                                        continue;
                                    }
                                }

                                var targetSeg = horizontalRoads[ti];
                                var targetAxisY = AxisY(targetSeg);
                                var t = (targetAxisY - outerAxisY) * inwardSign;
                                if (t < stepMin || t > stepMax)
                                {
                                    continue;
                                }

                                var score =
                                    (pass * 1000.0) +
                                    0.65 * Math.Min(
                                        Math.Abs(t - stepTargetPrimary),
                                        Math.Abs(t - stepTargetSecondary)) +
                                    0.35 * t;
                                if (targetHasTwentyCompanion)
                                {
                                    score -= 0.08;
                                }

                                if (targetSeg.Generated)
                                {
                                    score -= 0.03;
                                }

                                if (!found ||
                                    score < (bestScore - 1e-9) ||
                                    (Math.Abs(score - bestScore) <= 1e-9 && t < bestT))
                                {
                                    found = true;
                                    bestScore = score;
                                    bestT = t;
                                    bestTarget = new Point2d(southEndpoint.X, targetAxisY);
                                }
                            }
                        }
                    }

                    return found;
                }

                foreach (var id in secMovables)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1) || !IsHorizontalLike(p0, p1))
                    {
                        continue;
                    }

                    var moveFound = false;
                    var moveEndpointIndex = -1;
                    var moveTarget = default(Point2d);
                    var moveScore = double.MaxValue;
                    var moveT = double.MaxValue;
                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        scanned++;
                        var endpoint = endpointIndex == 0 ? p0 : p1;
                        var other = endpointIndex == 0 ? p1 : p0;
                        // SW rule: only adjust the west end of horizontal lines.
                        // Prevents accidentally pulling the SE endpoint westward.
                        if (endpoint.X > (other.X + 1e-6))
                        {
                            continue;
                        }
                        var inward = other - endpoint;
                        if (inward.Length <= 1e-6)
                        {
                            continue;
                        }

                        var inwardDir = inward / inward.Length;
                        Point2d bestEndpointTarget;
                        double bestScore;
                        double bestT;
                        bool endpointTouchedOuter;
                        bool endpointTouchedOuterThirtyEighteen;
                        var foundTarget = TryFindBestTarget(
                            endpoint,
                            inwardDir,
                            out bestEndpointTarget,
                            out bestScore,
                            out bestT,
                            out endpointTouchedOuter,
                            out endpointTouchedOuterThirtyEighteen);
                        if (endpointTouchedOuter)
                        {
                            touchingOuter++;
                        }
                        if (endpointTouchedOuterThirtyEighteen)
                        {
                            touchingOuterThirtyEighteen++;
                        }

                        if (!foundTarget)
                        {
                            continue;
                        }

                        candidates++;
                        if (!moveFound ||
                            bestScore < (moveScore - 1e-9) ||
                            (Math.Abs(bestScore - moveScore) <= 1e-9 && bestT < moveT))
                        {
                            moveFound = true;
                            moveEndpointIndex = endpointIndex;
                            moveTarget = bestEndpointTarget;
                            moveScore = bestScore;
                            moveT = bestT;
                        }
                    }

                    if (!moveFound || moveEndpointIndex < 0)
                    {
                        continue;
                    }

                    if (!TryMoveEndpointByIndex(writable, moveEndpointIndex, moveTarget, endpointMoveTol))
                    {
                        continue;
                    }

                    adjusted++;
                }

                var lsdHorizontalAdjusted = 0;
                for (var li = 0; li < lsdHorizontalMovables.Count; li++)
                {
                    var lsdId = lsdHorizontalMovables[li];
                    if (!(tr.GetObject(lsdId, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableLsd, out var l0, out var l1) || !IsHorizontalLike(l0, l1))
                    {
                        continue;
                    }

                    var moveFound = false;
                    var moveEndpointIndex = -1;
                    var moveTarget = default(Point2d);
                    var moveScore = double.MaxValue;
                    var moveT = double.MaxValue;
                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        scanned++;
                        var endpoint = endpointIndex == 0 ? l0 : l1;
                        var other = endpointIndex == 0 ? l1 : l0;
                        // Keep LSD horizontal correction on the west endpoint only.
                        if (endpoint.X > (other.X + 1e-6))
                        {
                            continue;
                        }
                        var inward = other - endpoint;
                        if (inward.Length <= 1e-6)
                        {
                            continue;
                        }

                        var inwardDir = inward / inward.Length;
                        Point2d bestEndpointTarget;
                        double bestScore;
                        double bestT;
                        bool endpointTouchedOuter;
                        bool endpointTouchedOuterThirtyEighteen;
                        var foundTarget = TryFindBestTarget(
                            endpoint,
                            inwardDir,
                            out bestEndpointTarget,
                            out bestScore,
                            out bestT,
                            out endpointTouchedOuter,
                            out endpointTouchedOuterThirtyEighteen);
                        if (endpointTouchedOuter)
                        {
                            touchingOuter++;
                        }
                        if (endpointTouchedOuterThirtyEighteen)
                        {
                            touchingOuterThirtyEighteen++;
                        }

                        if (!foundTarget)
                        {
                            continue;
                        }

                        candidates++;
                        if (!moveFound ||
                            bestScore < (moveScore - 1e-9) ||
                            (Math.Abs(bestScore - moveScore) <= 1e-9 && bestT < moveT))
                        {
                            moveFound = true;
                            moveEndpointIndex = endpointIndex;
                            moveTarget = bestEndpointTarget;
                            moveScore = bestScore;
                            moveT = bestT;
                        }
                    }

                    if (!moveFound || moveEndpointIndex < 0)
                    {
                        continue;
                    }

                    var moveLength = moveEndpointIndex == 0
                        ? l0.GetDistanceTo(moveTarget)
                        : l1.GetDistanceTo(moveTarget);
                    if (moveLength <= endpointMoveTol || moveLength > lsdMaxMove)
                    {
                        continue;
                    }

                    if (!TryMoveEndpointByIndex(writableLsd, moveEndpointIndex, moveTarget, endpointMoveTol))
                    {
                        continue;
                    }

                    lsdHorizontalAdjusted++;
                }

                var lsdVerticalAdjusted = 0;
                for (var li = 0; li < lsdVerticalMovables.Count; li++)
                {
                    var lsdId = lsdVerticalMovables[li];
                    if (!(tr.GetObject(lsdId, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableLsd, out var v0, out var v1) || !IsVerticalLike(v0, v1))
                    {
                        continue;
                    }

                    var southIndex = v0.Y <= v1.Y ? 0 : 1;
                    var southEndpoint = southIndex == 0 ? v0 : v1;
                    var northEndpoint = southIndex == 0 ? v1 : v0;
                    if (!TryFindBestVerticalLsdTarget(southEndpoint, northEndpoint, out var targetSouth))
                    {
                        continue;
                    }

                    var moveLength = southEndpoint.GetDistanceTo(targetSouth);
                    if (moveLength <= endpointMoveTol || moveLength > lsdMaxMove)
                    {
                        continue;
                    }

                    if (!TryMoveEndpointByIndex(writableLsd, southIndex, targetSouth, endpointMoveTol))
                    {
                        continue;
                    }

                    lsdVerticalAdjusted++;
                }

                lsdAdjusted = lsdHorizontalAdjusted + lsdVerticalAdjusted;

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: forced inward 10.06 from 30.16 outer boundary scanned={scanned}, touchingOuter={touchingOuter}, touchingOuter30.16={touchingOuterThirtyEighteen}, candidateHits={candidates}, secAdjusted={adjusted}, lsdAdjusted={lsdAdjusted} (h={lsdHorizontalAdjusted}, v={lsdVerticalAdjusted}).");
            }
        }

        private static void NormalizeThirtyEighteenCorridorLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
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
                var segments = new List<(
                    ObjectId Id,
                    string Layer,
                    Point2d A,
                    Point2d B,
                    bool Horizontal,
                    bool Vertical,
                    double Length,
                    double Coord,
                    double SpanMin,
                    double SpanMax)>();

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

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 25.0)
                    {
                        continue;
                    }

                    var coord = horizontal
                        ? (0.5 * (a.Y + b.Y))
                        : (0.5 * (a.X + b.X));
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical, len, coord, spanMin, spanMax));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                double OverlapAmount(
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length, double Coord, double SpanMin, double SpanMax) s1,
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length, double Coord, double SpanMin, double SpanMax) s2)
                {
                    return Math.Min(s1.SpanMax, s2.SpanMax) - Math.Max(s1.SpanMin, s2.SpanMin);
                }

                const double d10Min = 8.6;
                const double d10Max = 11.8;
                const double d20Min = 18.6;
                const double d20Max = 21.8;
                const double d30Min = 28.8;
                const double d30Max = 31.8;
                const double overlapMin = 18.0;
                static bool InRange(double value, double min, double max) => value >= min && value <= max;

                var usecVotes = new Dictionary<ObjectId, int>();
                void VoteUsec(ObjectId id)
                {
                    if (usecVotes.TryGetValue(id, out var n))
                    {
                        usecVotes[id] = n + 1;
                    }
                    else
                    {
                        usecVotes[id] = 1;
                    }
                }

                int RunOrientation(bool horizontal)
                {
                    var indexes = new List<int>();
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var s = segments[i];
                        if (horizontal ? s.Horizontal : s.Vertical)
                        {
                            indexes.Add(i);
                        }
                    }

                    var patternMatches = 0;
                    for (var mi = 0; mi < indexes.Count; mi++)
                    {
                        var mIdx = indexes[mi];
                        var m = segments[mIdx];
                        var near10 = new List<int>();
                        var near20 = new List<int>();
                        for (var oi = 0; oi < indexes.Count; oi++)
                        {
                            if (oi == mi)
                            {
                                continue;
                            }

                            var oIdx = indexes[oi];
                            var o = segments[oIdx];
                            if (OverlapAmount(m, o) < overlapMin)
                            {
                                continue;
                            }

                            var d = Math.Abs(m.Coord - o.Coord);
                            if (InRange(d, d10Min, d10Max))
                            {
                                near10.Add(oIdx);
                            }
                            else if (InRange(d, d20Min, d20Max))
                            {
                                near20.Add(oIdx);
                            }
                        }

                        if (near10.Count == 0 || near20.Count == 0)
                        {
                            continue;
                        }

                        var matchedThisMid = false;
                        for (var i10 = 0; i10 < near10.Count; i10++)
                        {
                            var a = segments[near10[i10]];
                            var sideA = Math.Sign(a.Coord - m.Coord);
                            if (sideA == 0)
                            {
                                continue;
                            }

                            for (var i20 = 0; i20 < near20.Count; i20++)
                            {
                                var b = segments[near20[i20]];
                                var sideB = Math.Sign(b.Coord - m.Coord);
                                if (sideB == 0 || sideA == sideB)
                                {
                                    continue;
                                }

                                if (OverlapAmount(a, b) < overlapMin)
                                {
                                    continue;
                                }

                                var dAB = Math.Abs(a.Coord - b.Coord);
                                if (!InRange(dAB, d30Min, d30Max))
                                {
                                    continue;
                                }

                                // 30.16 corridor pattern:
                                // middle 20.11 plus 10.06/20.11 companions are all L-USEC.
                                VoteUsec(m.Id);
                                VoteUsec(a.Id);
                                VoteUsec(b.Id);
                                matchedThisMid = true;
                            }
                        }

                        if (matchedThisMid)
                        {
                            patternMatches++;
                        }
                    }

                    return patternMatches;
                }

                var matchesH = RunOrientation(horizontal: true);
                var matchesV = RunOrientation(horizontal: false);

                if (usecVotes.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var normalizedToSec = 0;
                var normalizedToUsec = 0;
                foreach (var id in usecVotes.Keys)
                {
                    const string target = "L-USEC";

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, target, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = target;
                    writable.ColorIndex = 256;
                    if (string.Equals(target, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedToSec++;
                    }
                    else
                    {
                        normalizedToUsec++;
                    }
                }

                tr.Commit();
                if (normalizedToSec > 0 || normalizedToUsec > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: corridor-normalized {normalizedToSec} segment(s) to L-SEC and {normalizedToUsec} segment(s) to L-USEC (30.16 pattern matches H={matchesH}, V={matchesV}).");
                }
            }
        }

        private static void NormalizeUsecLayersToThreeBands(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyDictionary<ObjectId, int> sectionNumberByPolylineId,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
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
            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);

            bool IsPointInAnyWindow(Point2d point)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (point.X >= w.MinPoint.X && point.X <= w.MaxPoint.X &&
                        point.Y >= w.MinPoint.Y && point.Y <= w.MaxPoint.Y)
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

            bool IsBlindSouthBoundarySection(int sectionNumber)
            {
                return (sectionNumber >= 7 && sectionNumber <= 12) ||
                       (sectionNumber >= 19 && sectionNumber <= 24) ||
                       (sectionNumber >= 31 && sectionNumber <= 36);
            }

            bool IsSegmentOnBlindSouthBoundary(
                Point2d a,
                Point2d b,
                IReadOnlyList<(Point2d A, Point2d B, bool IsHorizontal)> blindBoundaries)
            {
                if (blindBoundaries == null || blindBoundaries.Count == 0 || a == b)
                {
                    return false;
                }

                const double boundaryDistanceTol = 0.60;
                const double overlapParamTolerance = 0.08;
                var midpoint = Midpoint(a, b);
                foreach (var boundary in blindBoundaries)
                {
                    if (IsHorizontalLike(a, b) != boundary.IsHorizontal)
                    {
                        continue;
                    }

                    if (DistancePointToSegment(a, boundary.A, boundary.B) > boundaryDistanceTol &&
                        DistancePointToSegment(b, boundary.A, boundary.B) > boundaryDistanceTol &&
                        DistancePointToSegment(midpoint, boundary.A, boundary.B) > boundaryDistanceTol)
                    {
                        continue;
                    }

                    var boundaryDir = boundary.B - boundary.A;
                    var boundaryLen2 = boundaryDir.DotProduct(boundaryDir);
                    if (boundaryLen2 <= 1e-9)
                    {
                        continue;
                    }

                    var t0 = (a - boundary.A).DotProduct(boundaryDir) / boundaryLen2;
                    var t1 = (b - boundary.A).DotProduct(boundaryDir) / boundaryLen2;
                    var overlapMin = Math.Max(Math.Min(t0, t1), 0.0);
                    var overlapMax = Math.Min(Math.Max(t0, t1), 1.0);
                    if (overlapMax - overlapMin < overlapParamTolerance)
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedSet = generatedRoadAllowanceIds == null
                    ? new HashSet<ObjectId>()
                    : new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));

                var blindSouthBoundarySegments = new List<(Point2d A, Point2d B, bool IsHorizontal)>(capacity: 12);
                if (sectionNumberByPolylineId != null)
                {
                    foreach (var pair in sectionNumberByPolylineId)
                    {
                        if (pair.Key.IsNull || !IsBlindSouthBoundarySection(pair.Value))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                        {
                            continue;
                        }

                        if (!TryGetQuarterAnchors(section, out var anchors))
                        {
                            anchors = GetFallbackAnchors(section);
                        }

                        var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                        var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                        if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                            !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
                        {
                            continue;
                        }

                        var boundaryIsHorizontal = IsHorizontalLike(sw, se);
                        var boundaryIsVertical = IsVerticalLike(sw, se);
                        if (!boundaryIsHorizontal && !boundaryIsVertical)
                        {
                            continue;
                        }

                        blindSouthBoundarySegments.Add((sw, se, boundaryIsHorizontal));
                    }
                }

                var roadSegments = new List<(ObjectId Id, bool IsUsecLayer, bool IsGenerated, bool IsHorizontal, bool IsBlindSouthBoundary, Point2d A, Point2d B, double Axis, double SpanMin, double SpanMax)>(
                    capacity: 256);
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsecLayer = IsUsecLayer(ent.Layer);
                    if (!isUsecLayer)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var isGenerated = generatedSet.Contains(id);
                    var len = a.GetDistanceTo(b);
                    if (len < 2.0)
                    {
                        continue;
                    }

                    var axis = horizontal
                        ? (0.5 * (a.Y + b.Y))
                        : (0.5 * (a.X + b.X));
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);

                    var isBlindSouthBoundary = IsSegmentOnBlindSouthBoundary(a, b, blindSouthBoundarySegments);
                    roadSegments.Add((id, isUsecLayer, isGenerated, horizontal, isBlindSouthBoundary, a, b, axis, spanMin, spanMax));
                }

                if (roadSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var generatedUsecIndices = new List<int>();
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    if (roadSegments[i].IsGenerated)
                    {
                        generatedUsecIndices.Add(i);
                    }
                }

                if (generatedUsecIndices.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                EnsureLayerWithColor(database, tr, LayerUsecZero, ResolveUsecLayerColorIndex(LayerUsecZero));
                EnsureLayerWithColor(database, tr, LayerUsecTwenty, ResolveUsecLayerColorIndex(LayerUsecTwenty));
                EnsureLayerWithColor(database, tr, LayerUsecThirty, ResolveUsecLayerColorIndex(LayerUsecThirty));

                const double overlapTolerance = 4.0;
                const double axisNeighborhoodTolerance = 55.0;
                const double bucketTolerance = 1.6;
                const double spanTouchTolerance = 6.0;
                const double twoLineSecGapTolerance = 1.2;
                const double twoLineThirtyGapTolerance = 1.6;
                const double twoLineMiddleToOuterGapTolerance = 1.4;
                const double lenTolerance = 0.35;
                const double secThirtyRatio = RoadAllowanceSecWidthMeters / RoadAllowanceUsecWidthMeters;
                const double rangeEdgeBand = 145.0;
                const double localTriadSecGapTolerance = 2.4;
                const double localTriadThirtyGapTolerance = 3.6;
                const double localTriadMinOverlap = 40.0;
                var middleToOuterGap = RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters;

                static bool HasSpanContact(
                    double aMin,
                    double aMax,
                    double bMin,
                    double bMax,
                    double minOverlap,
                    double maxGap)
                {
                    var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                    if (overlap >= minOverlap)
                    {
                        return true;
                    }

                    if (overlap >= 0.0)
                    {
                        return false;
                    }

                    return -overlap <= maxGap;
                }

                var normalizedToZero = 0;
                var normalizedToTwenty = 0;
                var normalizedToThirty = 0;
                var unchanged = 0;
                var rangeEdgeTwoLineZeroThirtyOverrides = 0;
                var localTriadZeroOverrides = 0;

                var toVisit = new List<int>(Math.Max(4, generatedUsecIndices.Count));
                var processed = new bool[roadSegments.Count];
                for (var startIndex = 0; startIndex < roadSegments.Count; startIndex++)
                {
                    if (processed[startIndex])
                    {
                        continue;
                    }

                    toVisit.Clear();
                    toVisit.Add(startIndex);
                    processed[startIndex] = true;
                    var component = new List<int>();
                    var componentHasGenerated = false;
                    for (var visit = 0; visit < toVisit.Count; visit++)
                    {
                        var i = toVisit[visit];
                        var seg = roadSegments[i];
                        component.Add(i);
                        componentHasGenerated |= seg.IsGenerated;

                        for (var j = 0; j < roadSegments.Count; j++)
                        {
                            if (processed[j])
                            {
                                continue;
                            }

                            var other = roadSegments[j];
                            if (seg.IsHorizontal != other.IsHorizontal)
                            {
                                continue;
                            }

                            if (Math.Abs(seg.Axis - other.Axis) > axisNeighborhoodTolerance)
                            {
                                continue;
                            }

                            if (!HasSpanContact(
                                seg.SpanMin,
                                seg.SpanMax,
                                other.SpanMin,
                                other.SpanMax,
                                overlapTolerance,
                                spanTouchTolerance))
                            {
                                continue;
                            }

                            processed[j] = true;
                            toVisit.Add(j);
                        }
                    }

                    if (component.Count == 0)
                    {
                        continue;
                    }

                    component.Sort((l, r) => roadSegments[l].Axis.CompareTo(roadSegments[r].Axis));

                    var buckets = new List<(double Axis, List<int> Members)>();
                    foreach (var idx in component)
                    {
                        var axis = roadSegments[idx].Axis;
                        if (buckets.Count == 0)
                        {
                            buckets.Add((axis, new List<int> { idx }));
                            continue;
                        }

                        var last = buckets[buckets.Count - 1];
                        if (Math.Abs(axis - last.Axis) <= bucketTolerance)
                        {
                            last.Members.Add(idx);
                            last = ((last.Axis * (last.Members.Count - 1) + axis) / last.Members.Count, last.Members);
                            buckets[buckets.Count - 1] = last;
                        }
                        else
                        {
                            buckets.Add((axis, new List<int> { idx }));
                        }
                    }

                    var bucketCount = buckets.Count;
                    if (!componentHasGenerated && bucketCount < 2)
                    {
                        // Avoid relayering isolated legacy singletons with no generated context.
                        continue;
                    }

                    var bucketLayers = new string[bucketCount];
                    var generatedInComponent = component.Exists(i => roadSegments[i].IsGenerated);
                    if (bucketCount == 1)
                    {
                        bucketLayers[0] = generatedInComponent
                            ? LayerUsecTwenty
                            : LayerUsecZero;
                    }
                    else if (bucketCount == 2)
                    {
                        var zeroAxis = buckets[0].Axis;
                        var farAxis = buckets[1].Axis;
                        var gap = Math.Abs(farAxis - zeroAxis);
                        var componentIsHorizontal = roadSegments[component[0]].IsHorizontal;
                        var nearWestRangeEdge = !componentIsHorizontal && zeroAxis <= (clipMinX + rangeEdgeBand);
                        var nearEastRangeEdge = !componentIsHorizontal && farAxis >= (clipMaxX - rangeEdgeBand);
                        var forceRangeEdgeZeroThirty = nearWestRangeEdge ^ nearEastRangeEdge;
                        var lowSplitThreshold = (middleToOuterGap + RoadAllowanceSecWidthMeters) * 0.5;
                        var highSplitThreshold = (RoadAllowanceSecWidthMeters + RoadAllowanceUsecWidthMeters) * 0.5;

                        if (Math.Abs(gap - RoadAllowanceSecWidthMeters) <= twoLineSecGapTolerance)
                        {
                            bucketLayers[0] = LayerUsecZero;
                            bucketLayers[1] = LayerUsecTwenty;
                        }
                        else if (Math.Abs(gap - middleToOuterGap) <= twoLineMiddleToOuterGapTolerance)
                        {
                            if (forceRangeEdgeZeroThirty)
                            {
                                if (nearWestRangeEdge)
                                {
                                    bucketLayers[0] = LayerUsecZero;
                                    bucketLayers[1] = LayerUsecThirty;
                                }
                                else
                                {
                                    bucketLayers[0] = LayerUsecThirty;
                                    bucketLayers[1] = LayerUsecZero;
                                }

                                rangeEdgeTwoLineZeroThirtyOverrides++;
                            }
                            else
                            {
                                bucketLayers[0] = LayerUsecTwenty;
                                bucketLayers[1] = LayerUsecThirty;
                            }
                        }
                        else if (Math.Abs(gap - RoadAllowanceUsecWidthMeters) <= twoLineThirtyGapTolerance)
                        {
                            bucketLayers[0] = LayerUsecZero;
                            bucketLayers[1] = LayerUsecThirty;
                        }
                        else if (gap <= (lowSplitThreshold - lenTolerance))
                        {
                            // Small two-line spacing most closely matches 20.12<->30.18.
                            if (forceRangeEdgeZeroThirty)
                            {
                                if (nearWestRangeEdge)
                                {
                                    bucketLayers[0] = LayerUsecZero;
                                    bucketLayers[1] = LayerUsecThirty;
                                }
                                else
                                {
                                    bucketLayers[0] = LayerUsecThirty;
                                    bucketLayers[1] = LayerUsecZero;
                                }

                                rangeEdgeTwoLineZeroThirtyOverrides++;
                            }
                            else
                            {
                                bucketLayers[0] = LayerUsecTwenty;
                                bucketLayers[1] = LayerUsecThirty;
                            }
                        }
                        else if (gap < (highSplitThreshold - lenTolerance))
                        {
                            // Mid spacing most closely matches 0<->20.12.
                            bucketLayers[0] = LayerUsecZero;
                            bucketLayers[1] = LayerUsecTwenty;
                        }
                        else
                        {
                            // Large spacing defaults to 0<->30.18.
                            bucketLayers[0] = LayerUsecZero;
                            bucketLayers[1] = LayerUsecThirty;
                        }
                    }
                    else
                    {
                        var zeroAxis = buckets[0].Axis;
                        var farAxis = buckets[bucketCount - 1].Axis;
                        var targetTwentyAxis = zeroAxis + ((farAxis - zeroAxis) * secThirtyRatio);
                        var twentyBucketIndex = 1;
                        var bestTwentyDistance = double.MaxValue;
                        for (var b = 1; b < bucketCount - 1; b++)
                        {
                            var d = Math.Abs(buckets[b].Axis - targetTwentyAxis);
                            if (d < bestTwentyDistance)
                            {
                                bestTwentyDistance = d;
                                twentyBucketIndex = b;
                            }
                        }

                        for (var b = 0; b < bucketCount; b++)
                        {
                            if (b == 0)
                            {
                                bucketLayers[b] = LayerUsecZero;
                                continue;
                            }

                            if (b == bucketCount - 1)
                            {
                                bucketLayers[b] = LayerUsecThirty;
                                continue;
                            }

                            if (b == twentyBucketIndex)
                            {
                                bucketLayers[b] = LayerUsecTwenty;
                                continue;
                            }

                            var axis = buckets[b].Axis;
                            var d0 = Math.Abs(axis - zeroAxis);
                            var d20 = Math.Abs(axis - targetTwentyAxis);
                            var d30 = Math.Abs(axis - farAxis);
                            if (d20 <= d0 && d20 <= d30)
                            {
                                bucketLayers[b] = LayerUsecTwenty;
                            }
                            else if (d0 <= d20 && d0 <= d30)
                            {
                                bucketLayers[b] = LayerUsecZero;
                            }
                            else
                            {
                                bucketLayers[b] = LayerUsecThirty;
                            }
                        }
                    }

                    bool BucketsHaveSpanOverlap(int leftBucketIndex, int rightBucketIndex, double minOverlap)
                    {
                        var leftMembers = buckets[leftBucketIndex].Members;
                        var rightMembers = buckets[rightBucketIndex].Members;
                        for (var li = 0; li < leftMembers.Count; li++)
                        {
                            var leftSeg = roadSegments[leftMembers[li]];
                            for (var ri = 0; ri < rightMembers.Count; ri++)
                            {
                                var rightSeg = roadSegments[rightMembers[ri]];
                                var overlap = Math.Min(leftSeg.SpanMax, rightSeg.SpanMax) - Math.Max(leftSeg.SpanMin, rightSeg.SpanMin);
                                if (overlap >= minOverlap)
                                {
                                    return true;
                                }
                            }
                        }

                        return false;
                    }

                    int FindBucketCompanion(
                        int sourceBucketIndex,
                        int sideSign,
                        double targetGap,
                        double gapTolerance,
                        double minOverlap)
                    {
                        var sourceAxis = buckets[sourceBucketIndex].Axis;
                        for (var bi = 0; bi < bucketCount; bi++)
                        {
                            if (bi == sourceBucketIndex)
                            {
                                continue;
                            }

                            var delta = buckets[bi].Axis - sourceAxis;
                            if (Math.Sign(delta) != sideSign)
                            {
                                continue;
                            }

                            var gap = Math.Abs(delta);
                            if (Math.Abs(gap - targetGap) > gapTolerance)
                            {
                                continue;
                            }

                            if (!BucketsHaveSpanOverlap(sourceBucketIndex, bi, minOverlap))
                            {
                                continue;
                            }

                            return bi;
                        }

                        return -1;
                    }

                    if (bucketCount >= 3)
                    {
                        for (var b = 0; b < bucketCount; b++)
                        {
                            if (!string.Equals(bucketLayers[b], LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var bucketHasGeneratedMember = false;
                            for (var mi = 0; mi < buckets[b].Members.Count; mi++)
                            {
                                if (roadSegments[buckets[b].Members[mi]].IsGenerated)
                                {
                                    bucketHasGeneratedMember = true;
                                    break;
                                }
                            }

                            if (bucketHasGeneratedMember)
                            {
                                continue;
                            }

                            var overrideToZero = false;
                            for (var side = -1; side <= 1; side += 2)
                            {
                                var secCompanion = FindBucketCompanion(
                                    b,
                                    side,
                                    RoadAllowanceSecWidthMeters,
                                    localTriadSecGapTolerance,
                                    localTriadMinOverlap);
                                if (secCompanion < 0)
                                {
                                    continue;
                                }

                                var thirtyCompanion = FindBucketCompanion(
                                    b,
                                    side,
                                    RoadAllowanceUsecWidthMeters,
                                    localTriadThirtyGapTolerance,
                                    localTriadMinOverlap);
                                if (thirtyCompanion < 0)
                                {
                                    continue;
                                }

                                overrideToZero = true;
                                break;
                            }

                            if (overrideToZero)
                            {
                                bucketLayers[b] = LayerUsecZero;
                                localTriadZeroOverrides++;
                            }
                        }
                    }

                    var componentContainsTraceTarget = component.Exists(i => IsTargetLayerTraceSegment(roadSegments[i].A, roadSegments[i].B));
                    if (componentContainsTraceTarget && logger != null)
                    {
                        logger.WriteLine(
                            $"LAYER-TARGET three-bands component axisKind={(roadSegments[component[0]].IsHorizontal ? "H" : "V")} bucketCount={bucketCount} generatedInComponent={generatedInComponent}.");
                        for (var b = 0; b < bucketCount; b++)
                        {
                            var axis = buckets[b].Axis;
                            var target = bucketLayers[b] ?? string.Empty;
                            logger.WriteLine(
                                $"LAYER-TARGET three-bands bucket idx={b + 1} axis={axis:0.###} target={target} members={buckets[b].Members.Count}.");
                            foreach (var member in buckets[b].Members)
                            {
                                var seg = roadSegments[member];
                                var isTrace = IsTargetLayerTraceSegment(seg.A, seg.B);
                                logger.WriteLine(
                                    $"LAYER-TARGET three-bands member id={seg.Id.Handle} layer={(seg.IsUsecLayer ? "USEC" : "OTHER")} generated={seg.IsGenerated} axis={seg.Axis:0.###} span=({seg.SpanMin:0.###},{seg.SpanMax:0.###}) trace={isTrace} a=({seg.A.X:0.###},{seg.A.Y:0.###}) b=({seg.B.X:0.###},{seg.B.Y:0.###}).");
                            }
                        }
                    }

                    for (var b = 0; b < bucketCount; b++)
                    {
                        var targetLayer = bucketLayers[b];
                        var members = buckets[b].Members;
                        foreach (var member in members)
                        {
                            var seg = roadSegments[member];
                            if ((seg.IsBlindSouthBoundary && seg.IsHorizontal) ||
                                !(tr.GetObject(seg.Id, OpenMode.ForWrite, false) is Entity writable) ||
                                writable.IsErased)
                            {
                                continue;
                            }

                            var existingLayer = writable.Layer ?? string.Empty;
                            if (string.Equals(existingLayer, targetLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                unchanged++;
                                continue;
                            }

                            var colorIndex = ResolveUsecLayerColorIndex(targetLayer);
                            EnsureLayerWithColor(database, tr, targetLayer, colorIndex);
                            writable.Layer = targetLayer;
                            writable.ColorIndex = 256;

                            if (string.Equals(targetLayer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase))
                            {
                                normalizedToThirty++;
                            }
                            else if (string.Equals(targetLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                            {
                                normalizedToTwenty++;
                            }
                            else
                            {
                                normalizedToZero++;
                            }
                        }
                    }
                }

                tr.Commit();
                var total = normalizedToZero + normalizedToTwenty + normalizedToThirty;
                if (total > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: normalized {total} usec segment(s) into three bands " +
                        $"[0:{normalizedToZero}, 20.11:{normalizedToTwenty}, 30.16:{normalizedToThirty}], unchanged={unchanged}, rangeEdge2Line0_30Overrides={rangeEdgeTwoLineZeroThirtyOverrides}, localTriad0Overrides={localTriadZeroOverrides}.");
                }
            }
        }

        private static void NormalizeBlindLineLayersBySecConnections(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
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

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical, a.GetDistanceTo(b)));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTol = 0.40;
                const double minBlindLength = 8.0;
                const double maxBlindLength = 320.0;
                var blindCandidates = 0;
                var secAnchoredBlind = 0;
                var normalized = 0;

                bool TouchesEndpoint(Point2d endpoint, (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length) seg)
                {
                    return DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointTol;
                }

                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (s.Length < minBlindLength || s.Length > maxBlindLength)
                    {
                        continue;
                    }

                    if (string.Equals(s.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var touchCountA = 0;
                    var touchCountB = 0;
                    var anchorSecAtA = false;
                    var anchorSecAtB = false;
                    for (var j = 0; j < segments.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var o = segments[j];
                        if ((s.Horizontal && !o.Vertical) || (s.Vertical && !o.Horizontal))
                        {
                            continue;
                        }

                        if (TouchesEndpoint(s.A, o))
                        {
                            touchCountA++;
                            if (string.Equals(o.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                            {
                                anchorSecAtA = true;
                            }
                        }

                        if (TouchesEndpoint(s.B, o))
                        {
                            touchCountB++;
                            if (string.Equals(o.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                            {
                                anchorSecAtB = true;
                            }
                        }
                    }

                    var aBlind = touchCountA > 0 && touchCountB == 0;
                    var bBlind = touchCountB > 0 && touchCountA == 0;
                    if (!aBlind && !bBlind)
                    {
                        continue;
                    }

                    blindCandidates++;
                    var anchoredOnSec = aBlind ? anchorSecAtA : anchorSecAtB;
                    if (!anchoredOnSec)
                    {
                        continue;
                    }

                    secAnchoredBlind++;
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(s.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = "L-SEC";
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                if (normalized > 0 || secAnchoredBlind > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: blind-line layer normalization candidates={blindCandidates}, secAnchored={secAnchoredBlind}, normalizedToSec={normalized}.");
                }
            }
        }

        private static bool IsWestRoadAllowanceCorrectionSection(int sectionNumber)
        {
            return (sectionNumber >= 7 && sectionNumber <= 12) ||
                   (sectionNumber >= 13 && sectionNumber <= 18) ||
                   (sectionNumber >= 19 && sectionNumber <= 24) ||
                   (sectionNumber >= 31 && sectionNumber <= 36);
        }

        private static void NormalizeWestRoadAllowanceBandsForKnownSections(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyDictionary<ObjectId, int> sectionNumberByPolylineId,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || sectionNumberByPolylineId == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var targetSections = new List<(
                    Point2d SwCorner,
                    Vector2d EastUnit,
                    Vector2d NorthUnit,
                    double MinV,
                    double MaxV,
                    double WestEdgeU,
                    double WestBandMinU,
                    double WestBandMaxU)>();
                foreach (var pair in sectionNumberByPolylineId)
                {
                    if (pair.Key.IsNull || !IsWestRoadAllowanceCorrectionSection(pair.Value))
                    {
                        continue;
                    }

                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                    {
                        continue;
                    }

                    if (!TryGetQuarterAnchors(section, out var anchors))
                    {
                        anchors = GetFallbackAnchors(section);
                    }

                    var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                    if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var swCorner))
                    {
                        var ext = section.GeometricExtents;
                        swCorner = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                    }

                    var minV = double.MaxValue;
                    var maxV = double.MinValue;
                    var westEdgeU = double.MaxValue;
                    for (var vi = 0; vi < section.NumberOfVertices; vi++)
                    {
                        var p = section.GetPoint2dAt(vi);
                        var rel = p - swCorner;
                        var v = rel.DotProduct(northUnit);
                        var u = rel.DotProduct(eastUnit);
                        if (v < minV)
                        {
                            minV = v;
                        }

                        if (v > maxV)
                        {
                            maxV = v;
                        }

                        if (u < westEdgeU)
                        {
                            westEdgeU = u;
                        }
                    }

                    if (minV >= maxV || westEdgeU == double.MaxValue)
                    {
                        continue;
                    }

                    var westBandMinU = westEdgeU - (RoadAllowanceUsecWidthMeters + 10.0);
                    var westBandMaxU = westEdgeU + 2.0;
                    targetSections.Add((swCorner, eastUnit, northUnit, minV, maxV, westEdgeU, westBandMinU, westBandMaxU));
                }

                if (targetSections.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var usecSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!IsUsecLayer(ent.Layer))
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

                    usecSegments.Add((id, a, b));
                }

                if (usecSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                EnsureLayerWithColor(database, tr, LayerUsecZero, ResolveUsecLayerColorIndex(LayerUsecZero));
                EnsureLayerWithColor(database, tr, LayerUsecTwenty, ResolveUsecLayerColorIndex(LayerUsecTwenty));
                EnsureLayerWithColor(database, tr, LayerUsecThirty, ResolveUsecLayerColorIndex(LayerUsecThirty));

                const double bucketTolerance = 1.6;
                const double sectionOverlapTol = 16.0;
                var corrected = 0;
                var correctedSections = 0;
                for (var si = 0; si < targetSections.Count; si++)
                {
                    var section = targetSections[si];
                    var candidates = new List<(ObjectId Id, double ULine)>();
                    for (var i = 0; i < usecSegments.Count; i++)
                    {
                        var seg = usecSegments[i];
                        var d = seg.B - seg.A;
                        var eastComp = Math.Abs(d.DotProduct(section.EastUnit));
                        var northComp = Math.Abs(d.DotProduct(section.NorthUnit));
                        if (northComp <= eastComp)
                        {
                            continue;
                        }

                        var relA = seg.A - section.SwCorner;
                        var relB = seg.B - section.SwCorner;
                        var uA = relA.DotProduct(section.EastUnit);
                        var uB = relB.DotProduct(section.EastUnit);
                        var vA = relA.DotProduct(section.NorthUnit);
                        var vB = relB.DotProduct(section.NorthUnit);
                        var minV = Math.Min(vA, vB);
                        var maxV = Math.Max(vA, vB);
                        var overlap = Math.Min(maxV, section.MaxV + sectionOverlapTol) - Math.Max(minV, section.MinV - sectionOverlapTol);
                        if (overlap < 20.0)
                        {
                            continue;
                        }

                        var uLine = 0.5 * (uA + uB);
                        if (uLine < section.WestBandMinU || uLine > section.WestBandMaxU)
                        {
                            continue;
                        }

                        candidates.Add((seg.Id, uLine));
                    }

                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    candidates.Sort((a, b) => a.ULine.CompareTo(b.ULine));
                    var buckets = new List<(double Axis, List<ObjectId> Members)>();
                    for (var i = 0; i < candidates.Count; i++)
                    {
                        var c = candidates[i];
                        if (buckets.Count == 0)
                        {
                            buckets.Add((c.ULine, new List<ObjectId> { c.Id }));
                            continue;
                        }

                        var last = buckets[buckets.Count - 1];
                        if (Math.Abs(c.ULine - last.Axis) <= bucketTolerance)
                        {
                            last.Members.Add(c.Id);
                            last = ((last.Axis * (last.Members.Count - 1) + c.ULine) / last.Members.Count, last.Members);
                            buckets[buckets.Count - 1] = last;
                        }
                        else
                        {
                            buckets.Add((c.ULine, new List<ObjectId> { c.Id }));
                        }
                    }

                    if (buckets.Count == 0)
                    {
                        continue;
                    }

                    var sectionAdjusted = false;
                    void ApplyBucketLayer(int bucketIndex, string targetLayer)
                    {
                        var members = buckets[bucketIndex].Members;
                        for (var mi = 0; mi < members.Count; mi++)
                        {
                            var id = members[mi];
                            if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                            {
                                continue;
                            }

                            if (string.Equals(writable.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            writable.Layer = targetLayer;
                            writable.ColorIndex = 256;
                            corrected++;
                            sectionAdjusted = true;
                        }
                    }

                    var thirtyAxis = section.WestEdgeU;
                    var twentyAxis = thirtyAxis - (RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters);
                    var zeroAxis = thirtyAxis - RoadAllowanceUsecWidthMeters;
                    for (var bi = 0; bi < buckets.Count; bi++)
                    {
                        var axis = buckets[bi].Axis;
                        var dZero = Math.Abs(axis - zeroAxis);
                        var dTwenty = Math.Abs(axis - twentyAxis);
                        var dThirty = Math.Abs(axis - thirtyAxis);
                        var targetLayer = LayerUsecTwenty;
                        if (dZero <= dTwenty && dZero <= dThirty)
                        {
                            targetLayer = LayerUsecZero;
                        }
                        else if (dThirty <= dZero && dThirty <= dTwenty)
                        {
                            targetLayer = LayerUsecThirty;
                        }
                        else
                        {
                            targetLayer = LayerUsecTwenty;
                        }

                        ApplyBucketLayer(bi, targetLayer);
                    }

                    if (sectionAdjusted)
                    {
                        correctedSections++;
                    }
                }

                tr.Commit();
                if (corrected > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: west RA section-band correction relayered {corrected} segment(s) across {correctedSections} section(s).");
                }
            }
        }

        private static void NormalizeUsecLayersBySectionEdgeOffsets(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyDictionary<ObjectId, int> sectionNumberByPolylineId,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || sectionNumberByPolylineId == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }
            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);

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

            bool IsBlindSouthBoundarySection(int sectionNumber)
            {
                return (sectionNumber >= 7 && sectionNumber <= 12) ||
                       (sectionNumber >= 19 && sectionNumber <= 24) ||
                       (sectionNumber >= 31 && sectionNumber <= 36);
            }

            bool IsSegmentOnBlindSouthBoundary(
                Point2d a,
                Point2d b,
                IReadOnlyList<(Point2d A, Point2d B, bool IsHorizontal)> blindBoundaries)
            {
                if (blindBoundaries == null || blindBoundaries.Count == 0 || a == b)
                {
                    return false;
                }

                const double boundaryDistanceTol = 0.60;
                const double overlapParamTolerance = 0.08;
                var midpoint = Midpoint(a, b);
                foreach (var boundary in blindBoundaries)
                {
                    if (IsHorizontalLike(a, b) != boundary.IsHorizontal)
                    {
                        continue;
                    }

                    if (DistancePointToSegment(a, boundary.A, boundary.B) > boundaryDistanceTol &&
                        DistancePointToSegment(b, boundary.A, boundary.B) > boundaryDistanceTol &&
                        DistancePointToSegment(midpoint, boundary.A, boundary.B) > boundaryDistanceTol)
                    {
                        continue;
                    }

                    var boundaryDir = boundary.B - boundary.A;
                    var boundaryLen2 = boundaryDir.DotProduct(boundaryDir);
                    if (boundaryLen2 <= 1e-9)
                    {
                        continue;
                    }

                    var t0 = (a - boundary.A).DotProduct(boundaryDir) / boundaryLen2;
                    var t1 = (b - boundary.A).DotProduct(boundaryDir) / boundaryLen2;
                    var overlapMin = Math.Max(Math.Min(t0, t1), 0.0);
                    var overlapMax = Math.Min(Math.Max(t0, t1), 1.0);
                    if (overlapMax - overlapMin < overlapParamTolerance)
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }

            static int LayerTieRank(string layer)
            {
                if (string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                if (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }

                if (string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }

                return 3;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blindSouthBoundarySegments = new List<(Point2d A, Point2d B, bool IsHorizontal)>(capacity: 12);
                var sectionFrames = new List<(
                    int SectionNumber,
                    Point2d Origin,
                    Vector2d EastUnit,
                    Vector2d NorthUnit,
                    double WestEdgeU,
                    double EastEdgeU,
                    double SouthEdgeV,
                    double NorthEdgeV)>();
                foreach (var pair in sectionNumberByPolylineId)
                {
                    if (pair.Key.IsNull)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                    {
                        continue;
                    }

                    if (!TryGetQuarterAnchors(section, out var anchors))
                    {
                        anchors = GetFallbackAnchors(section);
                    }

                    var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                    if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var origin))
                    {
                        var ext = section.GeometricExtents;
                        origin = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                    }

                    var westEdgeU = double.MaxValue;
                    var eastEdgeU = double.MinValue;
                    var southEdgeV = double.MaxValue;
                    var northEdgeV = double.MinValue;
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

                    if (westEdgeU >= eastEdgeU || southEdgeV >= northEdgeV)
                    {
                        continue;
                    }

                    if (IsBlindSouthBoundarySection(pair.Value) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
                    {
                        var boundaryIsHorizontal = IsHorizontalLike(sw, se);
                        var boundaryIsVertical = IsVerticalLike(sw, se);
                        if (boundaryIsHorizontal || boundaryIsVertical)
                        {
                            blindSouthBoundarySegments.Add((sw, se, boundaryIsHorizontal));
                        }
                    }

                    sectionFrames.Add((pair.Value, origin, eastUnit, northUnit, westEdgeU, eastEdgeU, southEdgeV, northEdgeV));
                }

                if (sectionFrames.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var segments = new List<(ObjectId Id, Point2d A, Point2d B, string Layer)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!IsUsecLayer(ent.Layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (IsHorizontalLike(a, b) &&
                        IsSegmentOnBlindSouthBoundary(a, b, blindSouthBoundarySegments))
                    {
                        continue;
                    }

                    if (a.GetDistanceTo(b) < 2.0)
                    {
                        continue;
                    }

                    segments.Add((id, a, b, ent.Layer ?? string.Empty));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                EnsureLayerWithColor(database, tr, LayerUsecZero, ResolveUsecLayerColorIndex(LayerUsecZero));
                EnsureLayerWithColor(database, tr, LayerUsecTwenty, ResolveUsecLayerColorIndex(LayerUsecTwenty));
                EnsureLayerWithColor(database, tr, LayerUsecThirty, ResolveUsecLayerColorIndex(LayerUsecThirty));

                const double axisTolerance = 3.6;
                const double overlapPadding = 16.0;
                const double minProjectedOverlap = 20.0;
                const double blindSouthTolerance = 1.2;
                const double zeroTwentyCompanionTol = 2.4;
                const double zeroTwentyCompanionMinOverlap = 40.0;
                const double rangeEdgeBand = 145.0;
                const double twentyThirtyCompanionTol = 2.4;
                const double twentyThirtyCompanionMinOverlap = 40.0;
                var middleToOuterGap = RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters;
                const double blindEndpointTol = 0.80;
                const double minBlindLength = 8.0;
                const double maxBlindLength = 320.0;
                const double maxRangeEdgeBlindLength = 140.0;
                var adjusted = 0;
                var unchanged = 0;
                var unresolved = 0;
                var skippedBlind = 0;
                var ownerResolved = 0;
                var fallbackResolved = 0;
                var preservedTwentyByZeroCompanion = 0;
                var forcedTwentyToZeroByRangeEdge = 0;
                var forcedTwentyToZeroBySecCompanion = 0;
                var forcedTwentyToZeroByGeomPattern = 0;
                var demotedBlindThirty = 0;
                var demotedBlindThirtyByRangeEdge = 0;
                var demotedBlindThirtyBySectionSide = 0;
                var demotedBlindThirtyByZeroAnchor = 0;
                var demotedBlindThirtyByTwentyAnchor = 0;

                static string CanonicalUsecLayer(string layer)
                {
                    if (string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                    {
                        return LayerUsecZero;
                    }

                    if (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                    {
                        return LayerUsecTwenty;
                    }

                    if (string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase))
                    {
                        return LayerUsecThirty;
                    }

                    if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        return LayerUsecBase;
                    }

                    return string.Empty;
                }

                static double ProjectedOverlap(
                    double aMin,
                    double aMax,
                    double bMin,
                    double bMax)
                {
                    return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                }

                bool HasParallelZeroCompanionAtTwentyOffset(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                    var segVertical = IsVerticalLike(seg.A, seg.B);
                    if (!segHorizontal && !segVertical)
                    {
                        return false;
                    }

                    var segAxis = segHorizontal
                        ? (0.5 * (seg.A.Y + seg.B.Y))
                        : (0.5 * (seg.A.X + seg.B.X));
                    var segSpanMin = segHorizontal
                        ? Math.Min(seg.A.X, seg.B.X)
                        : Math.Min(seg.A.Y, seg.B.Y);
                    var segSpanMax = segHorizontal
                        ? Math.Max(seg.A.X, seg.B.X)
                        : Math.Max(seg.A.Y, seg.B.Y);
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        if (!string.Equals(CanonicalUsecLayer(other.Layer), LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var otherHorizontal = IsHorizontalLike(other.A, other.B);
                        var otherVertical = IsVerticalLike(other.A, other.B);
                        if (segHorizontal != otherHorizontal || segVertical != otherVertical)
                        {
                            continue;
                        }

                        var otherAxis = segHorizontal
                            ? (0.5 * (other.A.Y + other.B.Y))
                            : (0.5 * (other.A.X + other.B.X));
                        var axisGap = Math.Abs(segAxis - otherAxis);
                        if (Math.Abs(axisGap - RoadAllowanceSecWidthMeters) > zeroTwentyCompanionTol)
                        {
                            continue;
                        }

                        var otherSpanMin = segHorizontal
                            ? Math.Min(other.A.X, other.B.X)
                            : Math.Min(other.A.Y, other.B.Y);
                        var otherSpanMax = segHorizontal
                            ? Math.Max(other.A.X, other.B.X)
                            : Math.Max(other.A.Y, other.B.Y);
                        var overlap = ProjectedOverlap(segSpanMin, segSpanMax, otherSpanMin, otherSpanMax);
                        if (overlap < zeroTwentyCompanionMinOverlap)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool TryFindParallelZeroCompanionAtTwentyOffset(int segmentIndex, out double companionAxis)
                {
                    companionAxis = double.NaN;
                    var seg = segments[segmentIndex];
                    var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                    var segVertical = IsVerticalLike(seg.A, seg.B);
                    if (!segHorizontal && !segVertical)
                    {
                        return false;
                    }

                    var segAxis = segHorizontal
                        ? (0.5 * (seg.A.Y + seg.B.Y))
                        : (0.5 * (seg.A.X + seg.B.X));
                    var segSpanMin = segHorizontal
                        ? Math.Min(seg.A.X, seg.B.X)
                        : Math.Min(seg.A.Y, seg.B.Y);
                    var segSpanMax = segHorizontal
                        ? Math.Max(seg.A.X, seg.B.X)
                        : Math.Max(seg.A.Y, seg.B.Y);
                    var bestOverlap = double.MinValue;
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        if (!string.Equals(CanonicalUsecLayer(other.Layer), LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var otherHorizontal = IsHorizontalLike(other.A, other.B);
                        var otherVertical = IsVerticalLike(other.A, other.B);
                        if (segHorizontal != otherHorizontal || segVertical != otherVertical)
                        {
                            continue;
                        }

                        var otherAxis = segHorizontal
                            ? (0.5 * (other.A.Y + other.B.Y))
                            : (0.5 * (other.A.X + other.B.X));
                        var axisGap = Math.Abs(segAxis - otherAxis);
                        if (Math.Abs(axisGap - RoadAllowanceSecWidthMeters) > zeroTwentyCompanionTol)
                        {
                            continue;
                        }

                        var otherSpanMin = segHorizontal
                            ? Math.Min(other.A.X, other.B.X)
                            : Math.Min(other.A.Y, other.B.Y);
                        var otherSpanMax = segHorizontal
                            ? Math.Max(other.A.X, other.B.X)
                            : Math.Max(other.A.Y, other.B.Y);
                        var overlap = ProjectedOverlap(segSpanMin, segSpanMax, otherSpanMin, otherSpanMax);
                        if (overlap < zeroTwentyCompanionMinOverlap)
                        {
                            continue;
                        }

                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            companionAxis = otherAxis;
                        }
                    }

                    return !double.IsNaN(companionAxis);
                }

                bool HasParallelTwentyCompanionAtTwentyOffset(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                    var segVertical = IsVerticalLike(seg.A, seg.B);
                    if (!segHorizontal && !segVertical)
                    {
                        return false;
                    }

                    var segAxis = segHorizontal
                        ? (0.5 * (seg.A.Y + seg.B.Y))
                        : (0.5 * (seg.A.X + seg.B.X));
                    var segSpanMin = segHorizontal
                        ? Math.Min(seg.A.X, seg.B.X)
                        : Math.Min(seg.A.Y, seg.B.Y);
                    var segSpanMax = segHorizontal
                        ? Math.Max(seg.A.X, seg.B.X)
                        : Math.Max(seg.A.Y, seg.B.Y);
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        if (!string.Equals(CanonicalUsecLayer(other.Layer), LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var otherHorizontal = IsHorizontalLike(other.A, other.B);
                        var otherVertical = IsVerticalLike(other.A, other.B);
                        if (segHorizontal != otherHorizontal || segVertical != otherVertical)
                        {
                            continue;
                        }

                        var otherAxis = segHorizontal
                            ? (0.5 * (other.A.Y + other.B.Y))
                            : (0.5 * (other.A.X + other.B.X));
                        var axisGap = Math.Abs(segAxis - otherAxis);
                        if (Math.Abs(axisGap - RoadAllowanceSecWidthMeters) > zeroTwentyCompanionTol)
                        {
                            continue;
                        }

                        var otherSpanMin = segHorizontal
                            ? Math.Min(other.A.X, other.B.X)
                            : Math.Min(other.A.Y, other.B.Y);
                        var otherSpanMax = segHorizontal
                            ? Math.Max(other.A.X, other.B.X)
                            : Math.Max(other.A.Y, other.B.Y);
                        var overlap = ProjectedOverlap(segSpanMin, segSpanMax, otherSpanMin, otherSpanMax);
                        if (overlap < zeroTwentyCompanionMinOverlap)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool IsVerticalNearRangeEdge(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    if (!IsVerticalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var segAxis = 0.5 * (seg.A.X + seg.B.X);
                    return segAxis <= (clipMinX + rangeEdgeBand) ||
                           segAxis >= (clipMaxX - rangeEdgeBand);
                }

                bool IsHorizontalNearRangeEdge(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    if (!IsHorizontalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var minX = Math.Min(seg.A.X, seg.B.X);
                    var maxX = Math.Max(seg.A.X, seg.B.X);
                    return minX <= (clipMinX + rangeEdgeBand) ||
                           maxX >= (clipMaxX - rangeEdgeBand);
                }

                bool IsHorizontalNearClipWindowRangeEdge(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    if (!IsHorizontalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var minX = Math.Min(seg.A.X, seg.B.X);
                    var maxX = Math.Max(seg.A.X, seg.B.X);
                    var minY = Math.Min(seg.A.Y, seg.B.Y);
                    var maxY = Math.Max(seg.A.Y, seg.B.Y);
                    const double minWindowOverlap = 8.0;
                    for (var wi = 0; wi < clipWindows.Count; wi++)
                    {
                        var w = clipWindows[wi];
                        var overlapY = Math.Min(maxY, w.MaxPoint.Y + overlapPadding) -
                                       Math.Max(minY, w.MinPoint.Y - overlapPadding);
                        if (overlapY < minWindowOverlap)
                        {
                            continue;
                        }

                        if (minX <= (w.MinPoint.X + rangeEdgeBand) ||
                            maxX >= (w.MaxPoint.X - rangeEdgeBand))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool EndpointTouchesOrthogonalUsec(int segmentIndex, Point2d endpoint)
                {
                    var seg = segments[segmentIndex];
                    var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                    var segVertical = IsVerticalLike(seg.A, seg.B);
                    if (!segHorizontal && !segVertical)
                    {
                        return false;
                    }

                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        var otherHorizontal = IsHorizontalLike(other.A, other.B);
                        var otherVertical = IsVerticalLike(other.A, other.B);
                        if ((segHorizontal && !otherVertical) || (segVertical && !otherHorizontal))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, other.A, other.B) <= blindEndpointTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool EndpointTouchesOrthogonalUsecLayer(int segmentIndex, Point2d endpoint, string canonicalLayer)
                {
                    var seg = segments[segmentIndex];
                    var segHorizontal = IsHorizontalLike(seg.A, seg.B);
                    var segVertical = IsVerticalLike(seg.A, seg.B);
                    if (!segHorizontal && !segVertical)
                    {
                        return false;
                    }

                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        var otherHorizontal = IsHorizontalLike(other.A, other.B);
                        var otherVertical = IsVerticalLike(other.A, other.B);
                        if ((segHorizontal && !otherVertical) || (segVertical && !otherHorizontal))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, other.A, other.B) > blindEndpointTol)
                        {
                            continue;
                        }

                        if (string.Equals(CanonicalUsecLayer(other.Layer), canonicalLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsOneSidedBlindUsec(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    var len = seg.A.GetDistanceTo(seg.B);
                    if (len < minBlindLength || len > maxBlindLength)
                    {
                        return false;
                    }

                    var touchA = EndpointTouchesOrthogonalUsec(segmentIndex, seg.A);
                    var touchB = EndpointTouchesOrthogonalUsec(segmentIndex, seg.B);
                    return touchA != touchB;
                }

                bool IsShortHorizontalRangeEdgeStub(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    var nearRangeEdge =
                        IsHorizontalNearRangeEdge(segmentIndex) ||
                        IsHorizontalNearClipWindowRangeEdge(segmentIndex);
                    if (!IsHorizontalLike(seg.A, seg.B) || !nearRangeEdge)
                    {
                        return false;
                    }

                    var len = seg.A.GetDistanceTo(seg.B);
                    return len >= minBlindLength && len <= maxRangeEdgeBlindLength;
                }

                bool IsShortHorizontalBlindStub(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    if (!IsHorizontalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var len = seg.A.GetDistanceTo(seg.B);
                    return len >= minBlindLength && len <= maxRangeEdgeBlindLength;
                }

                bool IsShortHorizontalSectionSideStub(int segmentIndex)
                {
                    var seg = segments[segmentIndex];
                    if (!IsHorizontalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var len = seg.A.GetDistanceTo(seg.B);
                    if (len < minBlindLength || len > maxRangeEdgeBlindLength)
                    {
                        return false;
                    }

                    for (var fi = 0; fi < sectionFrames.Count; fi++)
                    {
                        var section = sectionFrames[fi];
                        var relA = seg.A - section.Origin;
                        var relB = seg.B - section.Origin;
                        var uA = relA.DotProduct(section.EastUnit);
                        var uB = relB.DotProduct(section.EastUnit);
                        var vA = relA.DotProduct(section.NorthUnit);
                        var vB = relB.DotProduct(section.NorthUnit);

                        var minV = Math.Min(vA, vB);
                        var maxV = Math.Max(vA, vB);
                        var overlap = Math.Min(maxV, section.NorthEdgeV + overlapPadding) -
                                      Math.Max(minV, section.SouthEdgeV - overlapPadding);
                        if (overlap < minProjectedOverlap)
                        {
                            continue;
                        }

                        var aOnSideEdge =
                            Math.Abs(uA - section.WestEdgeU) <= axisTolerance ||
                            Math.Abs(uA - section.EastEdgeU) <= axisTolerance;
                        var bOnSideEdge =
                            Math.Abs(uB - section.WestEdgeU) <= axisTolerance ||
                            Math.Abs(uB - section.EastEdgeU) <= axisTolerance;
                        if (!aOnSideEdge && !bOnSideEdge)
                        {
                            continue;
                        }

                        var touchA = EndpointTouchesOrthogonalUsec(segmentIndex, seg.A);
                        var touchB = EndpointTouchesOrthogonalUsec(segmentIndex, seg.B);
                        if ((aOnSideEdge && touchA) || (bOnSideEdge && touchB))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindParallelSecCompanionAtSecOffset(int segmentIndex, out double companionAxis)
                {
                    companionAxis = double.NaN;
                    var seg = segments[segmentIndex];
                    if (!IsVerticalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var segAxis = 0.5 * (seg.A.X + seg.B.X);
                    var nearWest = segAxis <= (clipMinX + rangeEdgeBand);
                    var nearEast = segAxis >= (clipMaxX - rangeEdgeBand);
                    if (!nearWest && !nearEast)
                    {
                        return false;
                    }

                    var segSpanMin = Math.Min(seg.A.Y, seg.B.Y);
                    var segSpanMax = Math.Max(seg.A.Y, seg.B.Y);
                    var bestOverlap = double.MinValue;
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        if (!string.Equals(other.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                            !IsVerticalLike(other.A, other.B))
                        {
                            continue;
                        }

                        var otherAxis = 0.5 * (other.A.X + other.B.X);
                        if (nearWest && otherAxis <= segAxis)
                        {
                            continue;
                        }

                        if (nearEast && otherAxis >= segAxis)
                        {
                            continue;
                        }

                        var axisGap = Math.Abs(segAxis - otherAxis);
                        if (Math.Abs(axisGap - RoadAllowanceSecWidthMeters) > zeroTwentyCompanionTol)
                        {
                            continue;
                        }

                        var otherSpanMin = Math.Min(other.A.Y, other.B.Y);
                        var otherSpanMax = Math.Max(other.A.Y, other.B.Y);
                        var overlap = ProjectedOverlap(segSpanMin, segSpanMax, otherSpanMin, otherSpanMax);
                        if (overlap < zeroTwentyCompanionMinOverlap)
                        {
                            continue;
                        }

                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            companionAxis = otherAxis;
                        }
                    }

                    return !double.IsNaN(companionAxis);
                }

                bool TryFindParallelThirtyCompanionAtMiddleOffset(int segmentIndex, out double companionAxis)
                {
                    companionAxis = double.NaN;
                    var seg = segments[segmentIndex];
                    var segVertical = IsVerticalLike(seg.A, seg.B);
                    if (!segVertical)
                    {
                        return false;
                    }

                    var segAxis = 0.5 * (seg.A.X + seg.B.X);
                    var segSpanMin = Math.Min(seg.A.Y, seg.B.Y);
                    var segSpanMax = Math.Max(seg.A.Y, seg.B.Y);
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        if (!string.Equals(CanonicalUsecLayer(other.Layer), LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                            !IsVerticalLike(other.A, other.B))
                        {
                            continue;
                        }

                        var otherAxis = 0.5 * (other.A.X + other.B.X);
                        var axisGap = Math.Abs(segAxis - otherAxis);
                        if (Math.Abs(axisGap - middleToOuterGap) > twentyThirtyCompanionTol)
                        {
                            continue;
                        }

                        var otherSpanMin = Math.Min(other.A.Y, other.B.Y);
                        var otherSpanMax = Math.Max(other.A.Y, other.B.Y);
                        var overlap = ProjectedOverlap(segSpanMin, segSpanMax, otherSpanMin, otherSpanMax);
                        if (overlap < twentyThirtyCompanionMinOverlap)
                        {
                            continue;
                        }

                        companionAxis = otherAxis;
                        return true;
                    }

                    return false;
                }

                bool TryFindVerticalCompanionAtGap(
                    int segmentIndex,
                    double targetGap,
                    double gapTolerance,
                    double minOverlap,
                    Func<string, bool> layerPredicate,
                    out double companionAxis)
                {
                    companionAxis = double.NaN;
                    var seg = segments[segmentIndex];
                    if (!IsVerticalLike(seg.A, seg.B))
                    {
                        return false;
                    }

                    var segAxis = 0.5 * (seg.A.X + seg.B.X);
                    var segSpanMin = Math.Min(seg.A.Y, seg.B.Y);
                    var segSpanMax = Math.Max(seg.A.Y, seg.B.Y);
                    var bestOverlap = double.MinValue;
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segmentIndex)
                        {
                            continue;
                        }

                        var other = segments[oi];
                        if (!IsVerticalLike(other.A, other.B))
                        {
                            continue;
                        }

                        if (layerPredicate != null && !layerPredicate(other.Layer ?? string.Empty))
                        {
                            continue;
                        }

                        var otherAxis = 0.5 * (other.A.X + other.B.X);
                        var axisGap = Math.Abs(segAxis - otherAxis);
                        if (Math.Abs(axisGap - targetGap) > gapTolerance)
                        {
                            continue;
                        }

                        var otherSpanMin = Math.Min(other.A.Y, other.B.Y);
                        var otherSpanMax = Math.Max(other.A.Y, other.B.Y);
                        var overlap = ProjectedOverlap(segSpanMin, segSpanMax, otherSpanMin, otherSpanMax);
                        if (overlap < minOverlap)
                        {
                            continue;
                        }

                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            companionAxis = otherAxis;
                        }
                    }

                    return !double.IsNaN(companionAxis);
                }

                for (var si = 0; si < segments.Count; si++)
                {
                    var seg = segments[si];
                    var delta = seg.B - seg.A;
                    var ownerVotes = new Dictionary<string, (int Count, double Weight, double BestDistance)>(StringComparer.OrdinalIgnoreCase);
                    var fallbackVotes = new Dictionary<string, (int Count, double Weight, double BestDistance)>(StringComparer.OrdinalIgnoreCase);
                    var skipBlind = false;

                    void AddVote(
                        Dictionary<string, (int Count, double Weight, double BestDistance)> voteMap,
                        string layer,
                        double distance)
                    {
                        if (string.IsNullOrWhiteSpace(layer))
                        {
                            return;
                        }

                        if (!voteMap.TryGetValue(layer, out var score))
                        {
                            score = (0, 0.0, double.MaxValue);
                        }

                        score.Count++;
                        score.Weight += 1.0 / Math.Max(0.25, 1.0 + distance);
                        if (distance < score.BestDistance)
                        {
                            score.BestDistance = distance;
                        }

                        voteMap[layer] = score;
                    }

                    for (var fi = 0; fi < sectionFrames.Count; fi++)
                    {
                        var section = sectionFrames[fi];
                        var relA = seg.A - section.Origin;
                        var relB = seg.B - section.Origin;
                        var uA = relA.DotProduct(section.EastUnit);
                        var uB = relB.DotProduct(section.EastUnit);
                        var vA = relA.DotProduct(section.NorthUnit);
                        var vB = relB.DotProduct(section.NorthUnit);

                        var eastComp = Math.Abs(delta.DotProduct(section.EastUnit));
                        var northComp = Math.Abs(delta.DotProduct(section.NorthUnit));
                        if (northComp > eastComp)
                        {
                            var minV = Math.Min(vA, vB);
                            var maxV = Math.Max(vA, vB);
                            var overlap = Math.Min(maxV, section.NorthEdgeV + overlapPadding) -
                                          Math.Max(minV, section.SouthEdgeV - overlapPadding);
                            if (overlap < minProjectedOverlap)
                            {
                                continue;
                            }

                            var uLine = 0.5 * (uA + uB);
                            var bestOwnerLayer = string.Empty;
                            var bestOwnerDistance = double.MaxValue;
                            void ConsiderVerticalOwner(string layer, double axis)
                            {
                                var d = Math.Abs(uLine - axis);
                                if (d < bestOwnerDistance)
                                {
                                    bestOwnerDistance = d;
                                    bestOwnerLayer = layer;
                                }
                            }

                            ConsiderVerticalOwner(LayerUsecZero, section.WestEdgeU - RoadAllowanceUsecWidthMeters);
                            ConsiderVerticalOwner(LayerUsecTwenty, section.WestEdgeU - (RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters));
                            ConsiderVerticalOwner(LayerUsecThirty, section.WestEdgeU);

                            var bestFallbackLayer = string.Empty;
                            var bestFallbackDistance = double.MaxValue;
                            void ConsiderVerticalFallback(string layer, double axis)
                            {
                                var d = Math.Abs(uLine - axis);
                                if (d < bestFallbackDistance)
                                {
                                    bestFallbackDistance = d;
                                    bestFallbackLayer = layer;
                                }
                            }

                            ConsiderVerticalFallback(LayerUsecZero, section.EastEdgeU);
                            ConsiderVerticalFallback(LayerUsecTwenty, section.EastEdgeU + RoadAllowanceSecWidthMeters);
                            ConsiderVerticalFallback(LayerUsecThirty, section.EastEdgeU + RoadAllowanceUsecWidthMeters);

                            // Rule #14: vertical road allowances belong to the quarter/section on their right.
                            // Use west-edge votes as the owner-side source; east-edge votes are fallback only.
                            if (bestOwnerDistance <= axisTolerance)
                            {
                                AddVote(ownerVotes, bestOwnerLayer, bestOwnerDistance);
                            }

                            if (bestFallbackDistance <= axisTolerance)
                            {
                                AddVote(fallbackVotes, bestFallbackLayer, bestFallbackDistance);
                            }
                        }
                        else
                        {
                            var minU = Math.Min(uA, uB);
                            var maxU = Math.Max(uA, uB);
                            var overlap = Math.Min(maxU, section.EastEdgeU + overlapPadding) -
                                          Math.Max(minU, section.WestEdgeU - overlapPadding);
                            if (overlap < minProjectedOverlap)
                            {
                                continue;
                            }

                            var vLine = 0.5 * (vA + vB);
                            if (IsBlindSouthBoundarySection(section.SectionNumber) &&
                                Math.Abs(vLine - section.SouthEdgeV) <= blindSouthTolerance)
                            {
                                skipBlind = true;
                                break;
                            }

                            var bestOwnerLayer = string.Empty;
                            var bestOwnerDistance = double.MaxValue;
                            void ConsiderHorizontalOwner(string layer, double axis)
                            {
                                var d = Math.Abs(vLine - axis);
                                if (d < bestOwnerDistance)
                                {
                                    bestOwnerDistance = d;
                                    bestOwnerLayer = layer;
                                }
                            }

                            ConsiderHorizontalOwner(LayerUsecZero, section.SouthEdgeV - RoadAllowanceUsecWidthMeters);
                            ConsiderHorizontalOwner(LayerUsecTwenty, section.SouthEdgeV - (RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters));
                            ConsiderHorizontalOwner(LayerUsecThirty, section.SouthEdgeV);

                            var bestFallbackLayer = string.Empty;
                            var bestFallbackDistance = double.MaxValue;
                            void ConsiderHorizontalFallback(string layer, double axis)
                            {
                                var d = Math.Abs(vLine - axis);
                                if (d < bestFallbackDistance)
                                {
                                    bestFallbackDistance = d;
                                    bestFallbackLayer = layer;
                                }
                            }

                            ConsiderHorizontalFallback(LayerUsecZero, section.NorthEdgeV);
                            ConsiderHorizontalFallback(LayerUsecTwenty, section.NorthEdgeV + RoadAllowanceSecWidthMeters);
                            ConsiderHorizontalFallback(LayerUsecThirty, section.NorthEdgeV + RoadAllowanceUsecWidthMeters);

                            // Rule #15: horizontal road allowances belong to the quarter/section above.
                            // Use south-edge votes as the owner-side source; north-edge votes are fallback only.
                            if (bestOwnerDistance <= axisTolerance)
                            {
                                AddVote(ownerVotes, bestOwnerLayer, bestOwnerDistance);
                            }

                            if (bestFallbackDistance <= axisTolerance)
                            {
                                AddVote(fallbackVotes, bestFallbackLayer, bestFallbackDistance);
                            }
                        }
                    }

                    if (skipBlind)
                    {
                        skippedBlind++;
                        continue;
                    }

                    var useOwnerVotes = ownerVotes.Count > 0;
                    var selectedVotes = useOwnerVotes ? ownerVotes : fallbackVotes;
                    if (selectedVotes.Count == 0)
                    {
                        unresolved++;
                        continue;
                    }

                    if (useOwnerVotes)
                    {
                        ownerResolved++;
                    }
                    else
                    {
                        fallbackResolved++;
                    }

                    var targetLayer = selectedVotes
                        .OrderByDescending(v => v.Value.Count)
                        .ThenByDescending(v => v.Value.Weight)
                        .ThenBy(v => v.Value.BestDistance)
                        .ThenBy(v => LayerTieRank(v.Key))
                        .Select(v => v.Key)
                        .FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(targetLayer))
                    {
                        unresolved++;
                        continue;
                    }

                    var existingCanonicalLayer = CanonicalUsecLayer(seg.Layer);
                    var traceTargetSegment = IsTargetLayerTraceSegment(seg.A, seg.B);
                    var existingIsThirty = string.Equals(existingCanonicalLayer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
                    var existingIsBlindBase = string.Equals(existingCanonicalLayer, LayerUsecBase, StringComparison.OrdinalIgnoreCase);
                    if (string.Equals(existingCanonicalLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(targetLayer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) &&
                        HasParallelZeroCompanionAtTwentyOffset(si))
                    {
                        targetLayer = LayerUsecTwenty;
                        preservedTwentyByZeroCompanion++;
                    }
                    else if (string.Equals(existingCanonicalLayer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(targetLayer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) &&
                             HasParallelTwentyCompanionAtTwentyOffset(si))
                    {
                        targetLayer = LayerUsecZero;
                        preservedTwentyByZeroCompanion++;
                    }
                    else if (string.Equals(targetLayer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(existingCanonicalLayer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) &&
                             HasParallelZeroCompanionAtTwentyOffset(si))
                    {
                        // Geometric invariant:
                        // a segment with a same-orientation zero companion at 20.12m cannot be
                        // the 30.16 outer boundary; classify as 20.12 instead.
                        targetLayer = LayerUsecTwenty;
                        preservedTwentyByZeroCompanion++;
                    }

                    if (string.Equals(existingCanonicalLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(targetLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                        IsVerticalNearRangeEdge(si) &&
                        TryFindParallelThirtyCompanionAtMiddleOffset(si, out var thirtyCompanionAxis))
                    {
                        var segAxis = 0.5 * (seg.A.X + seg.B.X);
                        var nearWest = segAxis <= (clipMinX + rangeEdgeBand);
                        var nearEast = segAxis >= (clipMaxX - rangeEdgeBand);
                        var segOnOuterSide =
                            (nearWest && segAxis < thirtyCompanionAxis) ||
                            (nearEast && segAxis > thirtyCompanionAxis);
                        if (segOnOuterSide)
                        {
                            var hasZeroCompanion = TryFindParallelZeroCompanionAtTwentyOffset(si, out var zeroCompanionAxis);
                            var hasDirectionalOuterZero =
                                hasZeroCompanion &&
                                ((nearWest && zeroCompanionAxis < segAxis) ||
                                 (nearEast && zeroCompanionAxis > segAxis));
                            if (!hasDirectionalOuterZero)
                            {
                                targetLayer = LayerUsecZero;
                                forcedTwentyToZeroByRangeEdge++;
                            }
                        }
                    }
                    else if (string.Equals(existingCanonicalLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(targetLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                             IsVerticalNearRangeEdge(si) &&
                             TryFindParallelSecCompanionAtSecOffset(si, out _))
                    {
                        var segAxis = 0.5 * (seg.A.X + seg.B.X);
                        var nearWest = segAxis <= (clipMinX + rangeEdgeBand);
                        var nearEast = segAxis >= (clipMaxX - rangeEdgeBand);
                        var hasZeroCompanion = TryFindParallelZeroCompanionAtTwentyOffset(si, out var zeroCompanionAxis);
                        var hasDirectionalOuterZero =
                            hasZeroCompanion &&
                            ((nearWest && zeroCompanionAxis < segAxis) ||
                             (nearEast && zeroCompanionAxis > segAxis));
                        if (!hasDirectionalOuterZero)
                        {
                            targetLayer = LayerUsecZero;
                            forcedTwentyToZeroBySecCompanion++;
                        }
                    }
                    else if (string.Equals(existingCanonicalLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(targetLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                             TryFindVerticalCompanionAtGap(
                                 si,
                                 RoadAllowanceSecWidthMeters,
                                 zeroTwentyCompanionTol,
                                 zeroTwentyCompanionMinOverlap,
                                 layer =>
                                     string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(CanonicalUsecLayer(layer), LayerUsecTwenty, StringComparison.OrdinalIgnoreCase),
                                 out var secLikeAxis) &&
                             TryFindVerticalCompanionAtGap(
                                 si,
                                 RoadAllowanceUsecWidthMeters,
                                 axisTolerance,
                                 zeroTwentyCompanionMinOverlap,
                                 layer =>
                                 {
                                     var canonical = CanonicalUsecLayer(layer);
                                     return string.Equals(canonical, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(canonical, LayerUsecBase, StringComparison.OrdinalIgnoreCase);
                                 },
                                 out var thirtyLikeAxis))
                    {
                        var segAxis = 0.5 * (seg.A.X + seg.B.X);
                        var secDir = Math.Sign(secLikeAxis - segAxis);
                        var thirtyDir = Math.Sign(thirtyLikeAxis - segAxis);
                        var sameSide = secDir != 0 && secDir == thirtyDir;
                        if (sameSide)
                        {
                            var hasZeroCompanion = TryFindParallelZeroCompanionAtTwentyOffset(si, out var zeroCompanionAxis);
                            var zeroSameSide = hasZeroCompanion && Math.Sign(zeroCompanionAxis - segAxis) == secDir;
                            if (!zeroSameSide)
                            {
                                targetLayer = LayerUsecZero;
                                forcedTwentyToZeroByGeomPattern++;
                            }
                        }
                    }

                    // Adjacent/trimmed west/east blind stubs must not promote to 30.18.
                    // Keep these short horizontal blind lines on 20.11 (or 0 when already 0).
                    var shortHorizontalBlindStub = IsShortHorizontalBlindStub(si);
                    var touchesZeroA = EndpointTouchesOrthogonalUsecLayer(si, seg.A, LayerUsecZero);
                    var touchesZeroB = EndpointTouchesOrthogonalUsecLayer(si, seg.B, LayerUsecZero);
                    var touchesTwentyA = EndpointTouchesOrthogonalUsecLayer(si, seg.A, LayerUsecTwenty);
                    var touchesTwentyB = EndpointTouchesOrthogonalUsecLayer(si, seg.B, LayerUsecTwenty);
                    var touchesThirtyA = EndpointTouchesOrthogonalUsecLayer(si, seg.A, LayerUsecThirty);
                    var touchesThirtyB = EndpointTouchesOrthogonalUsecLayer(si, seg.B, LayerUsecThirty);
                    var hasThirtyAnchor = touchesThirtyA || touchesThirtyB;
                    var demoteThirtyByZeroAnchor =
                        shortHorizontalBlindStub &&
                        existingIsBlindBase &&
                        !hasThirtyAnchor &&
                        (touchesZeroA != touchesZeroB);
                    var demoteThirtyByTwentyAnchor =
                        shortHorizontalBlindStub &&
                        existingIsBlindBase &&
                        !hasThirtyAnchor &&
                        !demoteThirtyByZeroAnchor &&
                        (touchesTwentyA != touchesTwentyB);
                    var nearRangeEdge =
                        IsHorizontalNearRangeEdge(si) ||
                        IsHorizontalNearClipWindowRangeEdge(si);
                    var demoteThirtyByRangeEdge =
                        !existingIsThirty &&
                        nearRangeEdge &&
                        (IsOneSidedBlindUsec(si) || IsShortHorizontalRangeEdgeStub(si));
                    var demoteThirtyBySectionSide = !existingIsThirty && IsShortHorizontalSectionSideStub(si);
                    if (string.Equals(targetLayer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) &&
                        (demoteThirtyByZeroAnchor || demoteThirtyByTwentyAnchor || demoteThirtyByRangeEdge || demoteThirtyBySectionSide))
                    {
                        if (demoteThirtyByZeroAnchor)
                        {
                            targetLayer = LayerUsecBase;
                        }
                        else if (demoteThirtyByTwentyAnchor)
                        {
                            targetLayer = LayerUsecBase;
                        }
                        else
                        {
                            if (string.Equals(existingCanonicalLayer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                            {
                                targetLayer = LayerUsecZero;
                            }
                            else if (string.Equals(existingCanonicalLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                            {
                                targetLayer = LayerUsecTwenty;
                            }
                            else
                            {
                                targetLayer = LayerUsecBase;
                            }
                        }

                        demotedBlindThirty++;
                        if (demoteThirtyByZeroAnchor)
                        {
                            demotedBlindThirtyByZeroAnchor++;
                        }

                        if (demoteThirtyByTwentyAnchor)
                        {
                            demotedBlindThirtyByTwentyAnchor++;
                        }

                        if (demoteThirtyByRangeEdge)
                        {
                            demotedBlindThirtyByRangeEdge++;
                        }

                        if (demoteThirtyBySectionSide)
                        {
                            demotedBlindThirtyBySectionSide++;
                        }
                    }

                    if (traceTargetSegment && logger != null)
                    {
                        logger.WriteLine(
                            $"TRACE-RELAYER id={seg.Id.Handle} existing={existingCanonicalLayer} selected={targetLayer} shortStub={shortHorizontalBlindStub} zA={touchesZeroA} zB={touchesZeroB} tA={touchesTwentyA} tB={touchesTwentyB} thA={touchesThirtyA} thB={touchesThirtyB} demoteZ={demoteThirtyByZeroAnchor} demote20={demoteThirtyByTwentyAnchor} demoteRange={demoteThirtyByRangeEdge} demoteSide={demoteThirtyBySectionSide}.");
                    }

                    if (!(tr.GetObject(seg.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (string.Equals(writable.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        unchanged++;
                        continue;
                    }

                    writable.Layer = targetLayer;
                    writable.ColorIndex = 256;
                    adjusted++;
                }

                tr.Commit();
                if (adjusted > 0 || unresolved > 0 || skippedBlind > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: deterministic section-edge relayer adjusted={adjusted}, unchanged={unchanged}, unresolved={unresolved}, skippedBlind={skippedBlind}, ownerResolved={ownerResolved}, fallbackResolved={fallbackResolved}, preserved20ByZeroCompanion={preservedTwentyByZeroCompanion}, forced20To0RangeEdge={forcedTwentyToZeroByRangeEdge}, forced20To0SecCompanion={forcedTwentyToZeroBySecCompanion}, forced20To0GeomPattern={forcedTwentyToZeroByGeomPattern}, demotedBlind30To20={demotedBlindThirty}, demotedBlind30To20ZeroAnchor={demotedBlindThirtyByZeroAnchor}, demotedBlind30To20TwentyAnchor={demotedBlindThirtyByTwentyAnchor}, demotedBlind30To20RangeEdge={demotedBlindThirtyByRangeEdge}, demotedBlind30To20SectionSide={demotedBlindThirtyBySectionSide}.");
                }
            }
        }

        private static void NormalizeUsecCollinearComponentLayerConsistency(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyDictionary<ObjectId, int> sectionNumberByPolylineId,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || sectionNumberByPolylineId == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            bool IsBlindSouthBoundarySection(int sectionNumber)
            {
                return (sectionNumber >= 7 && sectionNumber <= 12) ||
                       (sectionNumber >= 19 && sectionNumber <= 24) ||
                       (sectionNumber >= 31 && sectionNumber <= 36);
            }

            bool IsSegmentOnBlindSouthBoundary(
                Point2d a,
                Point2d b,
                IReadOnlyList<(Point2d A, Point2d B, bool IsHorizontal)> blindBoundaries)
            {
                if (blindBoundaries == null || blindBoundaries.Count == 0 || a == b)
                {
                    return false;
                }

                const double boundaryDistanceTol = 0.60;
                const double overlapParamTolerance = 0.08;
                var midpoint = Midpoint(a, b);
                foreach (var boundary in blindBoundaries)
                {
                    if (IsHorizontalLike(a, b) != boundary.IsHorizontal)
                    {
                        continue;
                    }

                    if (DistancePointToSegment(a, boundary.A, boundary.B) > boundaryDistanceTol &&
                        DistancePointToSegment(b, boundary.A, boundary.B) > boundaryDistanceTol &&
                        DistancePointToSegment(midpoint, boundary.A, boundary.B) > boundaryDistanceTol)
                    {
                        continue;
                    }

                    var boundaryDir = boundary.B - boundary.A;
                    var boundaryLen2 = boundaryDir.DotProduct(boundaryDir);
                    if (boundaryLen2 <= 1e-9)
                    {
                        continue;
                    }

                    var t0 = (a - boundary.A).DotProduct(boundaryDir) / boundaryLen2;
                    var t1 = (b - boundary.A).DotProduct(boundaryDir) / boundaryLen2;
                    var overlapMin = Math.Max(Math.Min(t0, t1), 0.0);
                    var overlapMax = Math.Min(Math.Max(t0, t1), 1.0);
                    if (overlapMax - overlapMin < overlapParamTolerance)
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }

            static bool HasSpanContact(
                double aMin,
                double aMax,
                double bMin,
                double bMax,
                double minOverlap,
                double maxGap)
            {
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                if (overlap >= minOverlap)
                {
                    return true;
                }

                if (overlap >= 0.0)
                {
                    return false;
                }

                return -overlap <= maxGap;
            }

            static string CanonicalUsecLayer(string layer)
            {
                if (string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                {
                    return LayerUsecZero;
                }

                if (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                {
                    return LayerUsecTwenty;
                }

                if (string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase))
                {
                    return LayerUsecThirty;
                }

                if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                {
                    return LayerUsecBase;
                }

                return string.Empty;
            }

            static int LayerPriority(string layer)
            {
                if (string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase))
                {
                    return 4;
                }

                if (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }

                if (string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }

                if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }

                return 0;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blindSouthBoundarySegments = new List<(Point2d A, Point2d B, bool IsHorizontal)>(capacity: 12);
                foreach (var pair in sectionNumberByPolylineId)
                {
                    if (pair.Key.IsNull || !IsBlindSouthBoundarySection(pair.Value))
                    {
                        continue;
                    }

                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                    {
                        continue;
                    }

                    if (!TryGetQuarterAnchors(section, out var anchors))
                    {
                        anchors = GetFallbackAnchors(section);
                    }

                    var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                    if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                        !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
                    {
                        continue;
                    }

                    var boundaryIsHorizontal = IsHorizontalLike(sw, se);
                    var boundaryIsVertical = IsVerticalLike(sw, se);
                    if (!boundaryIsHorizontal && !boundaryIsVertical)
                    {
                        continue;
                    }

                    blindSouthBoundarySegments.Add((sw, se, boundaryIsHorizontal));
                }

                var segments = new List<(ObjectId Id, bool Horizontal, double Axis, double SpanMin, double SpanMax, string Layer)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!IsUsecLayer(ent.Layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    if (horizontal && IsSegmentOnBlindSouthBoundary(a, b, blindSouthBoundarySegments))
                    {
                        continue;
                    }

                    var axis = horizontal
                        ? 0.5 * (a.Y + b.Y)
                        : 0.5 * (a.X + b.X);
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    segments.Add((id, horizontal, axis, spanMin, spanMax, ent.Layer ?? string.Empty));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var adjusted = 0;
                var normalizedComponents = 0;
                const double axisTol = 0.9;
                const double overlapTol = 4.0;
                const double gapTol = 3.0;

                void ProcessOrientation(bool horizontal)
                {
                    var ordered = segments
                        .Select((s, idx) => (s, idx))
                        .Where(p => p.s.Horizontal == horizontal)
                        .OrderBy(p => p.s.Axis)
                        .ToList();
                    if (ordered.Count < 2)
                    {
                        return;
                    }

                    var axisGroups = new List<List<int>>();
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var idx = ordered[i].idx;
                        if (axisGroups.Count == 0)
                        {
                            axisGroups.Add(new List<int> { idx });
                            continue;
                        }

                        var lastGroup = axisGroups[axisGroups.Count - 1];
                        var refAxis = segments[lastGroup[lastGroup.Count - 1]].Axis;
                        if (Math.Abs(segments[idx].Axis - refAxis) <= axisTol)
                        {
                            lastGroup.Add(idx);
                        }
                        else
                        {
                            axisGroups.Add(new List<int> { idx });
                        }
                    }

                    for (var gi = 0; gi < axisGroups.Count; gi++)
                    {
                        var group = axisGroups[gi];
                        if (group.Count < 2)
                        {
                            continue;
                        }

                        var visited = new bool[group.Count];
                        for (var start = 0; start < group.Count; start++)
                        {
                            if (visited[start])
                            {
                                continue;
                            }

                            var queue = new Queue<int>();
                            var members = new List<int>();
                            visited[start] = true;
                            queue.Enqueue(start);
                            while (queue.Count > 0)
                            {
                                var cur = queue.Dequeue();
                                members.Add(group[cur]);
                                for (var other = 0; other < group.Count; other++)
                                {
                                    if (visited[other])
                                    {
                                        continue;
                                    }

                                    var a = segments[group[cur]];
                                    var b = segments[group[other]];
                                    if (!HasSpanContact(a.SpanMin, a.SpanMax, b.SpanMin, b.SpanMax, overlapTol, gapTol))
                                    {
                                        continue;
                                    }

                                    visited[other] = true;
                                    queue.Enqueue(other);
                                }
                            }

                            if (members.Count < 2)
                            {
                                continue;
                            }

                            var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            for (var m = 0; m < members.Count; m++)
                            {
                                var canonical = CanonicalUsecLayer(segments[members[m]].Layer);
                                if (string.IsNullOrWhiteSpace(canonical))
                                {
                                    continue;
                                }

                                if (!layerCounts.ContainsKey(canonical))
                                {
                                    layerCounts[canonical] = 0;
                                }

                                layerCounts[canonical]++;
                            }

                            if (layerCounts.Count <= 1)
                            {
                                continue;
                            }

                            var targetLayer = layerCounts
                                .OrderByDescending(p => p.Value)
                                .ThenByDescending(p => LayerPriority(p.Key))
                                .Select(p => p.Key)
                                .FirstOrDefault() ?? LayerUsecBase;

                            if (string.Equals(targetLayer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) &&
                                layerCounts.Keys.Any(k => !string.Equals(k, LayerUsecBase, StringComparison.OrdinalIgnoreCase)))
                            {
                                targetLayer = layerCounts
                                    .Where(p => !string.Equals(p.Key, LayerUsecBase, StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(p => p.Value)
                                    .ThenByDescending(p => LayerPriority(p.Key))
                                    .Select(p => p.Key)
                                    .FirstOrDefault() ?? targetLayer;
                            }

                            for (var m = 0; m < members.Count; m++)
                            {
                                var segment = segments[members[m]];
                                if (!(tr.GetObject(segment.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                                {
                                    continue;
                                }

                                var existingCanonical = CanonicalUsecLayer(writable.Layer ?? string.Empty);
                                if (string.IsNullOrWhiteSpace(existingCanonical) ||
                                    string.Equals(existingCanonical, targetLayer, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                writable.Layer = targetLayer;
                                writable.ColorIndex = 256;
                                adjusted++;
                            }

                            normalizedComponents++;
                        }
                    }
                }

                ProcessOrientation(horizontal: true);
                ProcessOrientation(horizontal: false);

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: normalized {adjusted} collinear L-USEC segment(s) across {normalizedComponents} mixed-layer component(s).");
                }
            }
        }

        private static void StitchTrimmedContextSectionEndpoints(
            Database database,
            IReadOnlyCollection<ObjectId> contextSectionIds,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || contextSectionIds == null || contextSectionIds.Count == 0 || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
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

                bool TryWriteOpenSegment(Entity ent, Point2d a, Point2d b)
                {
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (ent is Line ln)
                    {
                        ln.StartPoint = new Point3d(a.X, a.Y, ln.StartPoint.Z);
                        ln.EndPoint = new Point3d(b.X, b.Y, ln.EndPoint.Z);
                        return true;
                    }

                    if (ent is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        pl.SetPointAt(0, a);
                        pl.SetPointAt(1, b);
                        return true;
                    }

                    return false;
                }

                Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
                {
                    var ab = b - a;
                    var len2 = ab.DotProduct(ab);
                    if (len2 <= 1e-12)
                    {
                        return a;
                    }

                    var t = (p - a).DotProduct(ab) / len2;
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    return a + (ab * t);
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

                var contextSet = new HashSet<ObjectId>(contextSectionIds.Where(id => !id.IsNull));
                var allBoundarySegments = new List<(ObjectId Id, string Layer, bool IsContext, Point2d A, Point2d B)>();
                var endpointAnchors = new List<(ObjectId Id, string Layer, bool IsContext, Point2d P)>();
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

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
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

                    var isContext = contextSet.Contains(id);
                    var layerName = ent.Layer ?? string.Empty;
                    allBoundarySegments.Add((id, layerName, isContext, a, b));
                    endpointAnchors.Add((id, layerName, isContext, a));
                    endpointAnchors.Add((id, layerName, isContext, b));
                }

                if (allBoundarySegments.Count == 0 || endpointAnchors.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointSnapTol = 1.15;
                const double segmentSnapTol = 0.95;
                const double moveTol = 0.02;
                var adjusted = 0;
                foreach (var id in contextSet.ToList())
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var p0, out var p1))
                    {
                        continue;
                    }

                    var currentLayer = ent.Layer ?? string.Empty;
                    bool TryBestEndpointSnap(Point2d endpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDist = double.MaxValue;
                        var bestTargetIsContext = true;
                        for (var i = 0; i < endpointAnchors.Count; i++)
                        {
                            var anchor = endpointAnchors[i];
                            if (anchor.Id == id)
                            {
                                continue;
                            }

                            if (!string.Equals(anchor.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(anchor.P);
                            if (d > endpointSnapTol)
                            {
                                continue;
                            }

                            var prefer = !anchor.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = anchor.IsContext;
                            snapped = anchor.P;
                        }

                        return found;
                    }

                    bool TryBestSegmentSnap(Point2d endpoint, Point2d otherEndpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDist = double.MaxValue;
                        var thisIsHorizontal = IsHorizontalLike(endpoint, otherEndpoint);
                        var thisIsVertical = IsVerticalLike(endpoint, otherEndpoint);
                        for (var i = 0; i < allBoundarySegments.Count; i++)
                        {
                            var seg = allBoundarySegments[i];
                            if (seg.Id == id)
                            {
                                continue;
                            }

                            if (!string.Equals(seg.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var segIsHorizontal = IsHorizontalLike(seg.A, seg.B);
                            var segIsVertical = IsVerticalLike(seg.A, seg.B);
                            if ((thisIsHorizontal && !segIsHorizontal) ||
                                (thisIsVertical && !segIsVertical))
                            {
                                continue;
                            }

                            var candidate = ClosestPointOnSegment(endpoint, seg.A, seg.B);
                            var d = endpoint.GetDistanceTo(candidate);
                            if (d > segmentSnapTol)
                            {
                                continue;
                            }

                            if (!found || d < (bestDist - 1e-9))
                            {
                                found = true;
                                bestDist = d;
                                snapped = candidate;
                            }
                        }

                        return found;
                    }

                    var new0 = p0;
                    var new1 = p1;
                    if (!TryBestEndpointSnap(new0, out new0))
                    {
                        TryBestSegmentSnap(p0, p1, out new0);
                    }

                    if (!TryBestEndpointSnap(new1, out new1))
                    {
                        TryBestSegmentSnap(p1, p0, out new1);
                    }

                    if (new0.GetDistanceTo(p0) <= moveTol && new1.GetDistanceTo(p1) <= moveTol)
                    {
                        continue;
                    }

                    if (TryWriteOpenSegment(ent, new0, new1))
                    {
                        adjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: stitched {adjusted} trimmed context segment endpoint(s) to nearby final boundaries.");
                }
            }
        }
    }
}
