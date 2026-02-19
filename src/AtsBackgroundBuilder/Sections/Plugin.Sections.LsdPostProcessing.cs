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
        private static void RebuildLsdLabelsAtFinalIntersections(
            Database database,
            IEnumerable<QuarterLabelInfo> quarterInfos,
            Logger? logger)
        {
            if (database == null || quarterInfos == null)
            {
                return;
            }

            var uniqueQuarterInfos = new Dictionary<ObjectId, QuarterLabelInfo>();
            foreach (var info in quarterInfos)
            {
                if (info == null || info.QuarterId.IsNull || info.QuarterId.IsErased)
                {
                    continue;
                }

                if (!uniqueQuarterInfos.ContainsKey(info.QuarterId))
                {
                    uniqueQuarterInfos.Add(info.QuarterId, info);
                }
            }

            if (uniqueQuarterInfos.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, uniqueQuarterInfos.Keys, 100.0);
            bool IsPointInAnyWindow(Point2d p)
            {
                if (clipWindows == null || clipWindows.Count == 0)
                {
                    return true;
                }

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
                if (clipWindows == null || clipWindows.Count == 0)
                {
                    return true;
                }

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

                bool TryGetEntityCenter(Entity ent, out Point2d center)
                {
                    center = default;
                    if (ent == null)
                    {
                        return false;
                    }

                    try
                    {
                        var ext = ent.GeometricExtents;
                        center = new Point2d(
                            0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                            0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                        return true;
                    }
                    catch
                    {
                        if (ent is DBText dbt)
                        {
                            center = new Point2d(dbt.Position.X, dbt.Position.Y);
                            return true;
                        }

                        if (ent is MText mt)
                        {
                            center = new Point2d(mt.Location.X, mt.Location.Y);
                            return true;
                        }

                        if (ent is BlockReference br)
                        {
                            center = new Point2d(br.Position.X, br.Position.Y);
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

                bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection)
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
                    var u = Cross2d(diff, da) / denom;
                    if (t < -1e-6 || t > 1.0 + 1e-6 || u < -1e-6 || u > 1.0 + 1e-6)
                    {
                        return false;
                    }

                    intersection = a0 + (da * t);
                    return true;
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

                bool IsPointInExpandedExtents(Point2d p, Extents3d ext, double tol)
                {
                    return p.X >= (ext.MinPoint.X - tol) && p.X <= (ext.MaxPoint.X + tol) &&
                           p.Y >= (ext.MinPoint.Y - tol) && p.Y <= (ext.MaxPoint.Y + tol);
                }

                EnsureLayer(database, tr, "L-SECTION-LSD");
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var horizontalLsdSegments = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var verticalLsdSegments = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var oldLabelEntities = new Dictionary<ObjectId, Point2d>();
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

                    if (TryReadOpenSegment(ent, out var a, out var b))
                    {
                        if (!DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        var mid = Midpoint(a, b);
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            if (IsHorizontalLike(a, b))
                            {
                                horizontalLsdSegments.Add((a, b, mid));
                            }
                            else if (IsVerticalLike(a, b))
                            {
                                verticalLsdSegments.Add((a, b, mid));
                            }
                        }
                        else if (IsPointInAnyWindow(mid))
                        {
                            oldLabelEntities[id] = mid;
                        }

                        continue;
                    }

                    if (TryGetEntityCenter(ent, out var center) && IsPointInAnyWindow(center))
                    {
                        oldLabelEntities[id] = center;
                    }
                }

                if (horizontalLsdSegments.Count == 0 || verticalLsdSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var placed = 0;
                var erased = 0;
                var skipped = 0;
                var consumedOldLabelIds = new HashSet<ObjectId>();
                foreach (var pair in uniqueQuarterInfos)
                {
                    var info = pair.Value;
                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline quarter) || quarter.IsErased)
                    {
                        continue;
                    }

                    var blockName = GetLsdLabelBlockName(info.Quarter);
                    if (string.IsNullOrWhiteSpace(blockName))
                    {
                        continue;
                    }

                    Extents3d quarterExtents;
                    try
                    {
                        quarterExtents = quarter.GeometricExtents;
                    }
                    catch
                    {
                        skipped++;
                        continue;
                    }

                    var quarterCenter = new Point2d(
                        0.5 * (quarterExtents.MinPoint.X + quarterExtents.MaxPoint.X),
                        0.5 * (quarterExtents.MinPoint.Y + quarterExtents.MaxPoint.Y));
                    const double quarterTol = 2.0;
                    var horizontalCandidates = horizontalLsdSegments
                        .Where(s => IsPointInExpandedExtents(s.Mid, quarterExtents, quarterTol))
                        .ToList();
                    var verticalCandidates = verticalLsdSegments
                        .Where(s => IsPointInExpandedExtents(s.Mid, quarterExtents, quarterTol))
                        .ToList();
                    if (horizontalCandidates.Count == 0 || verticalCandidates.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    var foundTarget = false;
                    var target = default(Point2d);
                    var bestScore = double.MaxValue;
                    for (var hi = 0; hi < horizontalCandidates.Count; hi++)
                    {
                        var h = horizontalCandidates[hi];
                        for (var vi = 0; vi < verticalCandidates.Count; vi++)
                        {
                            var v = verticalCandidates[vi];
                            if (!TryIntersectSegments(h.A, h.B, v.A, v.B, out var intersection))
                            {
                                continue;
                            }

                            if (!IsPointInExpandedExtents(intersection, quarterExtents, quarterTol))
                            {
                                continue;
                            }

                            var score = quarterCenter.GetDistanceTo(intersection);
                            if (!foundTarget || score < bestScore)
                            {
                                foundTarget = true;
                                bestScore = score;
                                target = intersection;
                            }
                        }
                    }

                    if (!foundTarget)
                    {
                        var bestH = horizontalCandidates
                            .OrderBy(s => DistancePointToSegment(quarterCenter, s.A, s.B))
                            .First();
                        var bestV = verticalCandidates
                            .OrderBy(s => DistancePointToSegment(quarterCenter, s.A, s.B))
                            .First();
                        if (TryIntersectInfiniteLines(bestH.A, bestH.B, bestV.A, bestV.B, out var fallback) &&
                            IsPointInExpandedExtents(fallback, quarterExtents, 4.0))
                        {
                            foundTarget = true;
                            target = fallback;
                        }
                    }

                    if (!foundTarget)
                    {
                        skipped++;
                        continue;
                    }

                    foreach (var old in oldLabelEntities)
                    {
                        if (consumedOldLabelIds.Contains(old.Key))
                        {
                            continue;
                        }

                        if (!IsPointInExpandedExtents(old.Value, quarterExtents, quarterTol))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(old.Key, OpenMode.ForWrite, false) is Entity oldEnt) || oldEnt.IsErased)
                        {
                            continue;
                        }

                        oldEnt.Erase();
                        consumedOldLabelIds.Add(old.Key);
                        erased++;
                    }

                    InsertAndExplodeLsdLabelBlock(
                        database,
                        tr,
                        ms,
                        editor: null,
                        info.Quarter,
                        new Point3d(target.X, target.Y, 0.0),
                        "L-SECTION-LSD");
                    placed++;
                }

                tr.Commit();
                if (placed > 0 || erased > 0 || skipped > 0)
                {
                    logger?.WriteLine($"Cleanup: rebuilt {placed} LSD label block(s) at final quarter intersections (erased {erased} stale label entity/ies, skipped {skipped} quarter(s)).");
                }
            }
        }

        private static void SnapQuarterLsdLinesToSectionBoundaries(Database database, IEnumerable<ObjectId> requestedQuarterIds, Logger? logger)
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

                var sectionBoundarySegments = new List<(Point2d A, Point2d B)>();
                var lsdLineIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ent is Line)
                        {
                            lsdLineIds.Add(id);
                        }

                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var mid = Midpoint(a, b);
                    if (!IsPointInAnyWindow(mid))
                    {
                        continue;
                    }

                    sectionBoundarySegments.Add((a, b));
                }

                if (sectionBoundarySegments.Count == 0 || lsdLineIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var adjusted = 0;
                const double minT = 1e-3;
                const double endpointMoveTol = 0.05;

                foreach (var id in lsdLineIds)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Line ln) || ln.IsErased)
                    {
                        continue;
                    }

                    var p0 = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    var p1 = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (!IsAdjustableLsdLineSegment(p0, p1))
                    {
                        continue;
                    }

                    var center = Midpoint(p0, p1);
                    if (!IsPointInAnyWindow(center))
                    {
                        continue;
                    }

                    var dirVec = p1 - p0;
                    var len = dirVec.Length;
                    if (len <= 1e-4)
                    {
                        continue;
                    }

                    var dir = dirVec / len;
                    var tMin = -0.5 * len;
                    var tMax = 0.5 * len;
                    double? bestNeg = null;
                    double? bestPos = null;

                    foreach (var seg in sectionBoundarySegments)
                    {
                        if (!TryIntersectInfiniteLineWithSegment(center, dir, seg.A, seg.B, out var t))
                        {
                            continue;
                        }

                        if (t < -minT)
                        {
                            if (!bestNeg.HasValue || t > bestNeg.Value)
                            {
                                bestNeg = t;
                            }
                        }
                        else if (t > minT)
                        {
                            if (!bestPos.HasValue || t < bestPos.Value)
                            {
                                bestPos = t;
                            }
                        }
                    }

                    var newTMin = bestNeg ?? tMin;
                    var newTMax = bestPos ?? tMax;
                    if (newTMax - newTMin <= minT)
                    {
                        continue;
                    }

                    var newA = center + (dir * newTMin);
                    var newB = center + (dir * newTMax);
                    if (newA.GetDistanceTo(p0) <= endpointMoveTol && newB.GetDistanceTo(p1) <= endpointMoveTol)
                    {
                        continue;
                    }

                    ln.StartPoint = new Point3d(newA.X, newA.Y, ln.StartPoint.Z);
                    ln.EndPoint = new Point3d(newB.X, newB.Y, ln.EndPoint.Z);
                    adjusted++;
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {adjusted} L-SECTION-LSD line(s) to nearest L-SEC/L-USEC boundaries.");
                }
            }
        }

        private static void RecenterExplodedLsdLabelsToFinalLinework(
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

            bool TryGetEntityCenter(Entity ent, out Point2d center)
            {
                center = default;
                if (ent == null)
                {
                    return false;
                }

                try
                {
                    var ext = ent.GeometricExtents;
                    center = new Point2d(
                        0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                        0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                    return true;
                }
                catch
                {
                    if (ent is DBText dbt)
                    {
                        center = new Point2d(dbt.Position.X, dbt.Position.Y);
                        return true;
                    }

                    if (ent is MText mt)
                    {
                        center = new Point2d(mt.Location.X, mt.Location.Y);
                        return true;
                    }

                    if (ent is BlockReference br)
                    {
                        center = new Point2d(br.Position.X, br.Position.Y);
                        return true;
                    }
                }

                return false;
            }

            bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection)
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
                var u = Cross2d(diff, da) / denom;
                if (t < -1e-6 || t > 1.0 + 1e-6 || u < -1e-6 || u > 1.0 + 1e-6)
                {
                    return false;
                }

                intersection = a0 + (da * t);
                return true;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var horizontalLsdSegments = new List<(Point2d A, Point2d B)>();
                var verticalLsdSegments = new List<(Point2d A, Point2d B)>();
                var labelEntityCenters = new Dictionary<ObjectId, Point2d>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
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

                    if (TryReadOpenSegment(ent, out var a, out var b))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            if (!DoesSegmentIntersectAnyWindow(a, b))
                            {
                                continue;
                            }

                            if (IsHorizontalLike(a, b))
                            {
                                horizontalLsdSegments.Add((a, b));
                            }
                            else if (IsVerticalLike(a, b))
                            {
                                verticalLsdSegments.Add((a, b));
                            }

                            continue;
                        }

                        var shortMid = Midpoint(a, b);
                        if (IsPointInAnyWindow(shortMid))
                        {
                            labelEntityCenters[id] = shortMid;
                        }

                        continue;
                    }

                    if (TryGetEntityCenter(ent, out var center) && IsPointInAnyWindow(center))
                    {
                        labelEntityCenters[id] = center;
                    }
                }

                if (horizontalLsdSegments.Count == 0 ||
                    verticalLsdSegments.Count == 0 ||
                    labelEntityCenters.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var targetCenters = new List<Point2d>();
                var targetDedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var hi = 0; hi < horizontalLsdSegments.Count; hi++)
                {
                    var h = horizontalLsdSegments[hi];
                    for (var vi = 0; vi < verticalLsdSegments.Count; vi++)
                    {
                        var v = verticalLsdSegments[vi];
                        if (!TryIntersectSegments(h.A, h.B, v.A, v.B, out var intersection))
                        {
                            continue;
                        }

                        if (!IsPointInAnyWindow(intersection))
                        {
                            continue;
                        }

                        var key = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:0.###}|{1:0.###}",
                            intersection.X,
                            intersection.Y);
                        if (!targetDedupe.Add(key))
                        {
                            continue;
                        }

                        targetCenters.Add(intersection);
                    }
                }

                if (targetCenters.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double clusterJoinTol = 24.0;
                var remaining = new HashSet<ObjectId>(labelEntityCenters.Keys);
                var clusters = new List<List<ObjectId>>();
                while (remaining.Count > 0)
                {
                    var seed = remaining.First();
                    remaining.Remove(seed);

                    var cluster = new List<ObjectId> { seed };
                    var queue = new Queue<ObjectId>();
                    queue.Enqueue(seed);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        var currentCenter = labelEntityCenters[current];
                        var nearby = new List<ObjectId>();
                        foreach (var other in remaining)
                        {
                            if (currentCenter.GetDistanceTo(labelEntityCenters[other]) <= clusterJoinTol)
                            {
                                nearby.Add(other);
                            }
                        }

                        for (var i = 0; i < nearby.Count; i++)
                        {
                            var other = nearby[i];
                            remaining.Remove(other);
                            cluster.Add(other);
                            queue.Enqueue(other);
                        }
                    }

                    clusters.Add(cluster);
                }

                if (clusters.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var clusterInfos = new List<(List<ObjectId> Ids, Point2d Center, double ClosestDistance)>();
                for (var i = 0; i < clusters.Count; i++)
                {
                    var cluster = clusters[i];
                    var sumX = 0.0;
                    var sumY = 0.0;
                    for (var j = 0; j < cluster.Count; j++)
                    {
                        var c = labelEntityCenters[cluster[j]];
                        sumX += c.X;
                        sumY += c.Y;
                    }

                    var center = new Point2d(sumX / cluster.Count, sumY / cluster.Count);
                    var nearest = double.MaxValue;
                    for (var ti = 0; ti < targetCenters.Count; ti++)
                    {
                        var d = center.GetDistanceTo(targetCenters[ti]);
                        if (d < nearest)
                        {
                            nearest = d;
                        }
                    }

                    clusterInfos.Add((cluster, center, nearest));
                }

                var orderedClusters = clusterInfos
                    .OrderBy(ci => ci.ClosestDistance)
                    .ToList();

                var usedTargets = new bool[targetCenters.Count];
                const double maxSnapDistance = 80.0;
                const double minMoveTol = 0.05;
                const double ambiguousTargetDeltaTol = 1.5;
                var movedClusters = 0;
                var movedEntities = 0;
                var ambiguousClustersSkipped = 0;
                for (var i = 0; i < orderedClusters.Count; i++)
                {
                    var cluster = orderedClusters[i];
                    var bestTargetIndex = -1;
                    var bestDistance = double.MaxValue;
                    var secondBestDistance = double.MaxValue;
                    for (var ti = 0; ti < targetCenters.Count; ti++)
                    {
                        if (usedTargets[ti])
                        {
                            continue;
                        }

                        var d = cluster.Center.GetDistanceTo(targetCenters[ti]);
                        if (d < bestDistance)
                        {
                            secondBestDistance = bestDistance;
                            bestDistance = d;
                            bestTargetIndex = ti;
                        }
                        else if (d < secondBestDistance)
                        {
                            secondBestDistance = d;
                        }
                    }

                    if (bestTargetIndex < 0 || bestDistance > maxSnapDistance)
                    {
                        continue;
                    }

                    if (secondBestDistance < double.MaxValue &&
                        (secondBestDistance - bestDistance) <= ambiguousTargetDeltaTol)
                    {
                        ambiguousClustersSkipped++;
                        continue;
                    }

                    usedTargets[bestTargetIndex] = true;
                    if (bestDistance <= minMoveTol)
                    {
                        continue;
                    }

                    var target = targetCenters[bestTargetIndex];
                    var displacement = new Vector3d(
                        target.X - cluster.Center.X,
                        target.Y - cluster.Center.Y,
                        0.0);
                    var transform = Matrix3d.Displacement(displacement);
                    var movedAny = false;
                    for (var j = 0; j < cluster.Ids.Count; j++)
                    {
                        var id = cluster.Ids[j];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            writable.TransformBy(transform);
                            movedEntities++;
                            movedAny = true;
                        }
                        catch
                        {
                        }
                    }

                    if (movedAny)
                    {
                        movedClusters++;
                    }
                }

                tr.Commit();
                if (movedClusters > 0)
                {
                    logger?.WriteLine($"Cleanup: recentered {movedClusters} LSD label block cluster(s) to final LSD line intersections ({movedEntities} entity move(s)).");
                }
                if (ambiguousClustersSkipped > 0)
                {
                    logger?.WriteLine($"Cleanup: skipped {ambiguousClustersSkipped} ambiguous LSD label cluster snap(s).");
                }
            }
        }
    }
}
