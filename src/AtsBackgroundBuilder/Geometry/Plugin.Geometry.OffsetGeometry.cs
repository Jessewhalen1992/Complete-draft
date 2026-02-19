using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static List<Polyline> BuildBufferedQuarterOffsetPolylines(
            Database database,
            IEnumerable<ObjectId> quarterIds,
            double buffer,
            Logger? logger)
        {
            var result = new List<Polyline>();
            if (database == null || quarterIds == null || buffer <= 0.0)
            {
                return result;
            }

            var attempted = 0;
            var skippedInvalid = 0;
            var offsetFailed = 0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var id in quarterIds.Distinct())
                {
                    if (id.IsNull || id.IsErased)
                    {
                        skippedInvalid++;
                        continue;
                    }

                    var quarter = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (quarter == null || !quarter.Closed || quarter.NumberOfVertices < 3)
                    {
                        skippedInvalid++;
                        continue;
                    }

                    attempted++;
                    if (TryCreateOutsideOffsetPolyline(quarter, buffer, out var outside, logger))
                    {
                        result.Add(outside);
                    }
                    else
                    {
                        offsetFailed++;
                        logger?.WriteLine($"DEFPOINTS BUFFER: offset failed for quarter id {id}.");
                    }
                }

                tr.Commit();
            }

            logger?.WriteLine($"DEFPOINTS BUFFER: attempted={attempted}, created={result.Count}, skippedInvalid={skippedInvalid}, failed={offsetFailed}");
            return result;
        }

        private static bool TryCreateOutsideOffsetPolyline(Polyline source, double distance, [NotNullWhen(true)] out Polyline? outside, Logger? logger)
        {
            outside = null;
            var candidates = new List<Polyline>();
            try
            {
                CollectOffsetCandidates(source, distance, candidates);
                CollectOffsetCandidates(source, -distance, candidates);
                if (candidates.Count == 0)
                {
                    logger?.WriteLine("DEFPOINTS BUFFER: no offset candidates produced by GetOffsetCurves.");
                    return false;
                }

                Polyline? best = null;
                var bestArea = double.MinValue;
                foreach (var c in candidates)
                {
                    double area;
                    try { area = Math.Abs(c.Area); }
                    catch { area = 0.0; }
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = c;
                    }
                }

                if (best == null)
                {
                    logger?.WriteLine("DEFPOINTS BUFFER: candidates existed but no best offset selected.");
                    return false;
                }

                outside = (Polyline)best.Clone();
                logger?.WriteLine($"DEFPOINTS BUFFER: selected offset area={bestArea:0.###}, candidates={candidates.Count}");
                return true;
            }
            finally
            {
                foreach (var c in candidates)
                {
                    c.Dispose();
                }
            }
        }

        private static void CollectOffsetCandidates(Polyline source, double distance, List<Polyline> destination)
        {
            DBObjectCollection? offsets = null;
            try
            {
                offsets = source.GetOffsetCurves(distance);
                if (offsets == null)
                {
                    return;
                }

                foreach (DBObject obj in offsets)
                {
                    if (obj is Polyline pl && pl.Closed && pl.NumberOfVertices >= 3)
                    {
                        destination.Add((Polyline)pl.Clone());
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (offsets != null)
                {
                    foreach (DBObject obj in offsets)
                    {
                        obj.Dispose();
                    }
                }
            }
        }

        private static List<Polyline> BuildUnionBoundaries(List<Polyline> polylines, Logger? logger)
        {
            var output = new List<Polyline>();
            if (polylines == null || polylines.Count == 0)
            {
                return output;
            }

            Region? union = null;
            try
            {
                logger?.WriteLine($"DEFPOINTS BUFFER: union input count={polylines.Count}");
                foreach (var poly in polylines)
                {
                    var region = CreateRegionFromPolyline(poly);
                    if (region == null)
                    {
                        logger?.WriteLine("DEFPOINTS BUFFER: CreateRegionFromPolyline returned null for one offset.");
                        continue;
                    }

                    if (union == null)
                    {
                        union = region;
                    }
                    else
                    {
                        try
                        {
                            union.BooleanOperation(BooleanOperationType.BoolUnite, region);
                        }
                        finally
                        {
                            region.Dispose();
                        }
                    }
                }

                if (union == null)
                {
                    return output;
                }

                var exploded = new DBObjectCollection();
                union.Explode(exploded);
                var explodedCurves = new List<Curve>();
                foreach (DBObject obj in exploded)
                {
                    if (obj is Polyline pl && pl.Closed && pl.NumberOfVertices >= 3)
                    {
                        output.Add((Polyline)pl.Clone());
                    }
                    else if (obj is Curve curve)
                    {
                        explodedCurves.Add((Curve)curve.Clone());
                    }
                    obj.Dispose();
                }

                if (output.Count == 0)
                {
                    if (TryBuildClosedPolylinesFromCurves(explodedCurves, output))
                    {
                        logger?.WriteLine("DEFPOINTS BUFFER: union explode yielded curves; rebuilt closed boundary loop(s).");
                    }
                }

                foreach (var curve in explodedCurves)
                {
                    curve.Dispose();
                }

                if (output.Count == 0)
                {
                    // Fallback: keep the per-quarter offsets instead of collapsing into one large
                    // rectangle. This avoids pulling in unrelated neighboring sections.
                    foreach (var poly in polylines)
                    {
                        if (poly == null || !poly.Closed || poly.NumberOfVertices < 3)
                        {
                            continue;
                        }

                        output.Add((Polyline)poly.Clone());
                    }

                    if (output.Count > 0)
                    {
                        logger?.WriteLine("DEFPOINTS BUFFER: union explode empty, used per-offset fallback (no convex hull).");
                    }
                }
                logger?.WriteLine($"DEFPOINTS BUFFER: union output boundaries={output.Count}");
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("DEFPOINTS offset-union failed: " + ex.Message);
            }
            finally
            {
                union?.Dispose();
            }

            return output;
        }

        private static bool TryBuildClosedPolylinesFromCurves(List<Curve> curves, List<Polyline> output)
        {
            if (curves == null || curves.Count == 0 || output == null)
            {
                return false;
            }

            const double tol = 0.01;
            var segments = new List<(Point2d A, Point2d B)>();
            foreach (var curve in curves)
            {
                if (!(curve is Line line))
                {
                    continue;
                }

                var a = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                var b = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                if (a.GetDistanceTo(b) <= tol)
                {
                    continue;
                }

                segments.Add((a, b));
            }

            if (segments.Count < 3)
            {
                return false;
            }

            var builtAny = false;
            while (segments.Count > 0)
            {
                var loop = new List<Point2d>();
                var first = segments[0];
                segments.RemoveAt(0);
                loop.Add(first.A);
                loop.Add(first.B);

                var closed = false;
                while (true)
                {
                    var current = loop[loop.Count - 1];
                    if (current.GetDistanceTo(loop[0]) <= tol)
                    {
                        closed = true;
                        break;
                    }

                    var foundIndex = -1;
                    var next = default(Point2d);
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        if (current.GetDistanceTo(seg.A) <= tol)
                        {
                            foundIndex = i;
                            next = seg.B;
                            break;
                        }

                        if (current.GetDistanceTo(seg.B) <= tol)
                        {
                            foundIndex = i;
                            next = seg.A;
                            break;
                        }
                    }

                    if (foundIndex < 0)
                    {
                        break;
                    }

                    segments.RemoveAt(foundIndex);
                    loop.Add(next);
                }

                if (!closed || loop.Count < 4)
                {
                    continue;
                }

                var poly = new Polyline(loop.Count - 1) { Closed = true };
                for (var i = 0; i < loop.Count - 1; i++)
                {
                    poly.AddVertexAt(i, loop[i], 0, 0, 0);
                }

                if (poly.NumberOfVertices >= 3)
                {
                    output.Add(poly);
                    builtAny = true;
                }
                else
                {
                    poly.Dispose();
                }
            }

            return builtAny;
        }

        private static Polyline? BuildConvexHullPolyline(List<Polyline> polylines)
        {
            if (polylines == null || polylines.Count == 0)
            {
                return null;
            }

            var points = new List<Point2d>();
            foreach (var poly in polylines)
            {
                if (poly == null || poly.NumberOfVertices < 3)
                {
                    continue;
                }

                for (var i = 0; i < poly.NumberOfVertices; i++)
                {
                    points.Add(poly.GetPoint2dAt(i));
                }
            }

            var hull = ComputeConvexHull(points);
            if (hull.Count < 3)
            {
                return null;
            }

            var result = new Polyline(hull.Count) { Closed = true };
            for (var i = 0; i < hull.Count; i++)
            {
                result.AddVertexAt(i, hull[i], 0, 0, 0);
            }

            return result;
        }

        private static List<Point2d> ComputeConvexHull(List<Point2d> input)
        {
            var points = input
                .Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y))
                .Distinct(new Point2dApproxComparer(1e-6))
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (points.Count <= 1)
            {
                return points;
            }

            var lower = new List<Point2d>();
            foreach (var p in points)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0.0)
                {
                    lower.RemoveAt(lower.Count - 1);
                }
                lower.Add(p);
            }

            var upper = new List<Point2d>();
            for (var i = points.Count - 1; i >= 0; i--)
            {
                var p = points[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0.0)
                {
                    upper.RemoveAt(upper.Count - 1);
                }
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point2d a, Point2d b, Point2d c)
        {
            var ab = b - a;
            var ac = c - a;
            return (ab.X * ac.Y) - (ab.Y * ac.X);
        }

        private sealed class Point2dApproxComparer : IEqualityComparer<Point2d>
        {
            private readonly double _eps;

            public Point2dApproxComparer(double eps)
            {
                _eps = Math.Max(1e-9, eps);
            }

            public bool Equals(Point2d x, Point2d y)
            {
                return Math.Abs(x.X - y.X) <= _eps && Math.Abs(x.Y - y.Y) <= _eps;
            }

            public int GetHashCode(Point2d obj)
            {
                var qx = Math.Round(obj.X / _eps);
                var qy = Math.Round(obj.Y / _eps);
                return HashCode.Combine(qx, qy);
            }
        }

        private static Region? CreateRegionFromPolyline(Polyline polyline)
        {
            DBObjectCollection? curves = null;
            DBObjectCollection? regions = null;
            try
            {
                curves = new DBObjectCollection();
                curves.Add((Curve)polyline.Clone());
                regions = Region.CreateFromCurves(curves);
                if (regions == null || regions.Count == 0)
                {
                    return null;
                }

                var region = (Region)regions[0];
                for (var i = 1; i < regions.Count; i++)
                {
                    regions[i]?.Dispose();
                }
                return region;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (curves != null)
                {
                    foreach (DBObject c in curves)
                    {
                        c.Dispose();
                    }
                }
            }
        }

        private static List<Extents2d> MergeIntersectingExtents(List<Extents2d> input)
        {
            var remaining = new List<Extents2d>(input);
            var merged = new List<Extents2d>();
            while (remaining.Count > 0)
            {
                var current = remaining[0];
                remaining.RemoveAt(0);
                var expanded = true;
                while (expanded)
                {
                    expanded = false;
                    for (var i = remaining.Count - 1; i >= 0; i--)
                    {
                        var other = remaining[i];
                        if (!Extents2dIntersects(current, other))
                        {
                            continue;
                        }

                        current = UnionExtents2d(current, other);
                        remaining.RemoveAt(i);
                        expanded = true;
                    }
                }

                merged.Add(current);
            }

            return merged;
        }

        private static bool Extents2dIntersects(Extents2d a, Extents2d b)
        {
            return !(a.MaxPoint.X < b.MinPoint.X ||
                     a.MinPoint.X > b.MaxPoint.X ||
                     a.MaxPoint.Y < b.MinPoint.Y ||
                     a.MinPoint.Y > b.MaxPoint.Y);
        }

        private static Extents2d UnionExtents2d(Extents2d a, Extents2d b)
        {
            return new Extents2d(
                new Point2d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y)),
                new Point2d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y)));
        }
    }
}
