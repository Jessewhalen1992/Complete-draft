/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public static class GeometryUtils
    {
        public static bool ExtentsIntersect(Extents3d a, Extents3d b)
        {
            // AutoCAD's Extents3d does not provide an IsDisjoint helper in all versions.
            // Treat touching as intersecting.
            return !(
                a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
                a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y ||
                a.MaxPoint.Z < b.MinPoint.Z || a.MinPoint.Z > b.MaxPoint.Z
            );
        }

        public static bool ExtentsIntersect(Extents2d a, Extents2d b)
        {
            // Treat touching as intersecting.
            return !(
                a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
                a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y
            );
        }

        /// <summary>
        /// Compute intersection regions between two closed polylines.
        /// Caller owns disposal of returned regions.
        /// </summary>
        public static bool TryIntersectRegions(Polyline subject, Polyline clip, out List<Region> regions)
        {
            regions = new List<Region>();

            Region? subjectRegion = null;
            Region? clipRegion = null;

            try
            {
                subjectRegion = CreateRegion(subject);
                clipRegion = CreateRegion(clip);

                if (subjectRegion == null || clipRegion == null)
                    return false;

                subjectRegion.BooleanOperation(BooleanOperationType.BoolIntersect, clipRegion);

                // Note: BooleanOperation modifies the subject region in place.
                regions.Add(subjectRegion);
                subjectRegion = null; // prevent disposal here; caller will dispose

                return regions.Count > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                clipRegion?.Dispose();
                subjectRegion?.Dispose();
            }
        }

        public static bool TryIntersectPolylines(Polyline subject, Polyline clip, out List<Polyline> pieces)
        {
            pieces = new List<Polyline>();

            if (subject == null || clip == null)
                return false;

            Region? subjectRegion = null;
            Region? clipRegion = null;
            DBObjectCollection? exploded = null;

            try
            {
                subjectRegion = CreateRegion(subject);
                clipRegion = CreateRegion(clip);

                if (subjectRegion == null || clipRegion == null)
                    return false;

                subjectRegion.BooleanOperation(BooleanOperationType.BoolIntersect, clipRegion);

                exploded = new DBObjectCollection();
                subjectRegion.Explode(exploded);

                foreach (DBObject obj in exploded)
                {
                    if (obj is Polyline pl)
                    {
                        pieces.Add(pl);
                    }
                    else
                    {
                        obj.Dispose();
                    }
                }

                return pieces.Count > 0;
            }
            catch
            {
                foreach (var piece in pieces)
                    piece.Dispose();
                pieces.Clear();
                return false;
            }
            finally
            {
                if (exploded != null)
                {
                    foreach (DBObject obj in exploded)
                    {
                        if (!(obj is Polyline pl && pieces.Any(piece => ReferenceEquals(piece, pl))))
                            obj.Dispose();
                    }
                }

                clipRegion?.Dispose();
                subjectRegion?.Dispose();
            }
        }

        public static Polyline? IntersectPolylineWithRect(Polyline poly, Point3d min, Point3d max)
        {
            using var rect = new Polyline(4);
            rect.AddVertexAt(0, new Point2d(min.X, min.Y), 0, 0, 0);
            rect.AddVertexAt(1, new Point2d(max.X, min.Y), 0, 0, 0);
            rect.AddVertexAt(2, new Point2d(max.X, max.Y), 0, 0, 0);
            rect.AddVertexAt(3, new Point2d(min.X, max.Y), 0, 0, 0);
            rect.Closed = true;
            if (GeometryUtils.TryIntersectPolylines(poly, rect, out var pieces) && pieces.Count > 0)
                return pieces.OrderByDescending(p => p.Length).First();
            return null;
        }

        private static Region? CreateRegion(Polyline polyline)
        {
            try
            {
                var curves = new DBObjectCollection();
                curves.Add((Curve)polyline.Clone());

                var regions = Region.CreateFromCurves(curves);
                if (regions == null || regions.Count == 0)
                    return null;

                // Keep first region; dispose extras
                var first = (Region)regions[0];
                for (int i = 1; i < regions.Count; i++)
                {
                    regions[i]?.Dispose();
                }
                return first;
            }
            catch
            {
                return null;
            }
        }

        public static IEnumerable<Point2d> GetSpiralOffsets(Point2d center, double step, int maxPoints)
        {
            // Simple spiral: center, then 8 directions at increasing radius
            yield return center;

            int yielded = 1;
            int ring = 1;

            while (yielded < maxPoints)
            {
                for (int i = 0; i < 8 && yielded < maxPoints; i++)
                {
                    double angle = (Math.PI / 4.0) * i;
                    yield return new Point2d(
                        center.X + Math.Cos(angle) * step * ring,
                        center.Y + Math.Sin(angle) * step * ring
                    );
                    yielded++;
                }
                ring++;
            }
        }

        public static Point2d GetSafeInteriorPoint(Polyline polyline)
        {
            // Try extents center first
            var ext = polyline.GeometricExtents;
            var center = new Point2d(
                (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0
            );

            if (IsPointInsidePolyline(polyline, center))
                return center;

            // Spiral search
            double step = Math.Max(polyline.Length / 200.0, 1.0);
            for (int r = 1; r <= 50; r++)
            {
                for (int i = 0; i < 8; i++)
                {
                    double angle = (Math.PI / 4.0) * i;
                    var p = new Point2d(
                        center.X + Math.Cos(angle) * step * r,
                        center.Y + Math.Sin(angle) * step * r
                    );
                    if (IsPointInsidePolyline(polyline, p))
                        return p;
                }
            }

            return center;
        }

        public static bool IsPointInsidePolyline(Polyline polyline, Point2d point)
{
    if (polyline == null) return false;
    if (!polyline.Closed) return false;

    int n = polyline.NumberOfVertices;
    if (n < 3) return false;

    bool inside = false;

    for (int i = 0, j = n - 1; i < n; j = i++)
    {
        var pi = polyline.GetPoint2dAt(i);
        var pj = polyline.GetPoint2dAt(j);

        // Consider points on the boundary as inside.
        if (IsPointOnSegment(point, pj, pi, 1e-9))
            return true;

        bool intersects = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                          (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y + 0.0) + pi.X);

        if (intersects)
            inside = !inside;
    }

    return inside;
}

private static bool IsPointOnSegment(Point2d p, Point2d a, Point2d b, double tol)
{
    var ab = b - a;
    var ap = p - a;

    double cross = ab.X * ap.Y - ab.Y * ap.X;
    if (Math.Abs(cross) > tol) return false;

    double dot = ap.X * ab.X + ap.Y * ab.Y;
    if (dot < -tol) return false;

    double len2 = ab.X * ab.X + ab.Y * ab.Y;
    if (dot > len2 + tol) return false;

    return true;
}

        // --------------------------------------------------------------------
        // Width measurement utilities (for ROW-style disposition polygons)
        // --------------------------------------------------------------------

        public static bool TryWidthAtPoint(Polyline corridor, Point2d center, out double width)
        {
            width = 0;

            double param;
            try
            {
                param = corridor.GetParameterAtPoint(new Point3d(center.X, center.Y, 0));
            }
            catch
            {
                return false;
            }

            Vector3d deriv = corridor.GetFirstDerivative(param);
            Vector2d tan2d = new Vector2d(deriv.X, deriv.Y);
            if (tan2d.Length < 1e-6)
                return false;

            Vector2d normal = new Vector2d(-tan2d.Y, tan2d.X).GetNormal();
            double halfLen = corridor.GeometricExtents.MaxPoint.DistanceTo(corridor.GeometricExtents.MinPoint) * 2.0;
            return TryCrossSectionWidth(corridor, center, normal, halfLen, out width);
        }

        public readonly struct WidthMeasurement
        {
            public WidthMeasurement(
                double medianWidth,
                double minWidth,
                double maxWidth,
                bool isVariable,
                bool usedSamples,
                Point2d medianCenter)
            {
                MedianWidth = medianWidth;
                MinWidth = minWidth;
                MaxWidth = maxWidth;
                IsVariable = isVariable;
                UsedSamples = usedSamples;
                MedianCenter = medianCenter;
            }

            public double MedianWidth { get; }
            public double MinWidth { get; }
            public double MaxWidth { get; }
            public bool IsVariable { get; }
            public bool UsedSamples { get; }
            public Point2d MedianCenter { get; }
        }

        /// <summary>
        /// Measures the "corridor width" of a closed polyline by sampling cross-sections perpendicular to its principal axis.
        /// If sampling fails, falls back to an oriented bounding width.
        /// </summary>
        public static WidthMeasurement MeasureCorridorWidth(
            Polyline corridor,
            int sampleCount,
            double variableAbsTol,
            double variableRelTol)
        {
            if (corridor == null) throw new ArgumentNullException(nameof(corridor));
            if (sampleCount < 1) sampleCount = 7;

            // Determine principal axes
            if (!TryGetPrincipalAxes(corridor, out var origin, out var major, out var minor))
            {
                origin = GetSafeInteriorPoint(corridor);
                major = Vector2d.XAxis;
                minor = Vector2d.YAxis;
            }

            // Compute ranges in principal space (using vertices)
            GetPrincipalRanges(corridor, origin, major, minor, out double minT, out double maxT, out double minS, out double maxS);

            var widths = new List<double>(sampleCount);
            var samples = new List<(double width, Point2d center)>(sampleCount);
            bool usedSamples = false;

            // Sample positions (skip ends) using local polyline normal
            double length = corridor.Length;
            for (int i = 1; i <= sampleCount; i++)
            {
                double frac = (double)i / (sampleCount + 1);
                double dist = frac * length;
                double param = corridor.GetParameterAtDistance(dist);
                Point3d p3d = corridor.GetPointAtParameter(param);
                Point2d center2d = new Point2d(p3d.X, p3d.Y);

                Vector3d derivative = corridor.GetFirstDerivative(param);
                Vector2d tan2d = new Vector2d(derivative.X, derivative.Y);
                if (tan2d.Length > 1e-6)
                {
                    Vector2d normal = new Vector2d(-tan2d.Y, tan2d.X).GetNormal();
                    double bigLocal = Math.Max(corridor.GeometricExtents.MaxPoint.DistanceTo(corridor.GeometricExtents.MinPoint), length) * 2.0;
                    double probe = 0.5;
                    Point2d plus = new Point2d(center2d.X + normal.X * probe, center2d.Y + normal.Y * probe);
                    Point2d minus = new Point2d(center2d.X - normal.X * probe, center2d.Y - normal.Y * probe);

                    Point2d inside;
                    if (IsPointInsidePolyline(corridor, plus))
                        inside = plus;
                    else if (IsPointInsidePolyline(corridor, minus))
                        inside = minus;
                    else
                        continue;

                    if (TryCrossSectionWidthStraddlingPoint(corridor, inside, normal, bigLocal, out var w, out var mid))
                    {
                        widths.Add(w);
                        samples.Add((w, mid));
                        usedSamples = true;
                    }
                }
            }

            if (widths.Count == 0)
            {
                // Fallback: oriented bounding width
                double fallback = maxS - minS;
                if (fallback < 0) fallback = -fallback;
                var fallbackCenter = GetSafeInteriorPoint(corridor);
                return new WidthMeasurement(fallback, fallback, fallback, false, false, fallbackCenter);
            }

            widths.Sort();
            double preliminaryMedian = widths[widths.Count / 2];
            double maxAllowed = preliminaryMedian * 3;
            widths = widths.FindAll(w => w <= maxAllowed);
            samples = samples.FindAll(sample => sample.width <= maxAllowed);

            if (widths.Count == 0)
            {
                double fallback = maxS - minS;
                if (fallback < 0) fallback = -fallback;
                var fallbackCenter = GetSafeInteriorPoint(corridor);
                return new WidthMeasurement(fallback, fallback, fallback, false, false, fallbackCenter);
            }

            widths.Sort();
            samples.Sort((a, b) => a.width.CompareTo(b.width));
            double median = widths[widths.Count / 2];
            double minW = widths[0];
            double maxW = widths[widths.Count - 1];
            double range = maxW - minW;

            bool isVariable = range > Math.Max(variableAbsTol, median * variableRelTol);

            Point2d medianCenter = samples.Count > 0 ? samples[0].center : GetSafeInteriorPoint(corridor);
            double bestDiff = double.MaxValue;
            foreach (var sample in samples)
            {
                double diff = Math.Abs(sample.width - median);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    medianCenter = sample.center;
                }
            }

            return new WidthMeasurement(median, minW, maxW, isVariable, usedSamples, medianCenter);
        }

        public static double SnapWidthToAcceptable(double measured, IReadOnlyList<double> acceptable, double tolerance)
        {
            if (acceptable == null || acceptable.Count == 0) return measured;
            if (tolerance < 0) tolerance = 0;

            double best = measured;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < acceptable.Count; i++)
            {
                var w = acceptable[i];
                double diff = Math.Abs(measured - w);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = w;
                }
            }

            return bestDiff <= tolerance ? best : measured;
        }

        private static bool TryCrossSectionWidthStraddlingPoint(
            Polyline corridor,
            Point2d insidePoint,
            Vector2d direction,
            double halfLength,
            out double width,
            out Point2d midPoint)
        {
            width = 0;
            midPoint = insidePoint;

            try
            {
                var dir = direction;
                if (dir.Length < 1e-9) return false;
                dir = dir.GetNormal();

                var p1 = new Point3d(insidePoint.X - dir.X * halfLength, insidePoint.Y - dir.Y * halfLength, corridor.Elevation);
                var p2 = new Point3d(insidePoint.X + dir.X * halfLength, insidePoint.Y + dir.Y * halfLength, corridor.Elevation);

                using (var line = new Line(p1, p2))
                {
                    var pts = new Point3dCollection();
                    corridor.IntersectWith(line, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                    if (pts.Count < 2) return false;

                    var proj = new List<double>(pts.Count);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var p = pts[i];
                        double s = (p.X - insidePoint.X) * dir.X + (p.Y - insidePoint.Y) * dir.Y;

                        bool dup = false;
                        for (int j = 0; j < proj.Count; j++)
                        {
                            if (Math.Abs(proj[j] - s) < 1e-6) { dup = true; break; }
                        }
                        if (!dup) proj.Add(s);
                    }

                    if (proj.Count < 2) return false;

                    proj.Sort();

                    const double eps = 1e-6;
                    bool haveNeg = false, havePos = false;
                    double sNeg = double.NegativeInfinity;
                    double sPos = double.PositiveInfinity;

                    foreach (var s in proj)
                    {
                        if (s < -eps && s > sNeg) { sNeg = s; haveNeg = true; }
                        if (s > eps && s < sPos) { sPos = s; havePos = true; }
                    }

                    if (!haveNeg || !havePos) return false;

                    width = sPos - sNeg;
                    if (width <= 1e-6) return false;

                    double midS = (sNeg + sPos) * 0.5;
                    midPoint = new Point2d(insidePoint.X + dir.X * midS, insidePoint.Y + dir.Y * midS);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPrincipalAxes(Polyline pl, out Point2d origin, out Vector2d major, out Vector2d minor)
        {
            origin = new Point2d(0, 0);
            major = Vector2d.XAxis;
            minor = Vector2d.YAxis;

            int n = pl.NumberOfVertices;
            if (n < 3) return false;

            double meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                meanX += p.X;
                meanY += p.Y;
            }
            meanX /= n;
            meanY /= n;

            origin = new Point2d(meanX, meanY);

            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                double dx = p.X - meanX;
                double dy = p.Y - meanY;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            if (Math.Abs(sxy) < 1e-9 && Math.Abs(sxx - syy) < 1e-9)
                return false;

            double angle = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
            major = new Vector2d(Math.Cos(angle), Math.Sin(angle));
            if (major.Length < 1e-9) return false;
            major = major.GetNormal();

            minor = new Vector2d(-major.Y, major.X); // 90 deg
            if (minor.Length < 1e-9) return false;
            minor = minor.GetNormal();

            return true;
        }

        private static void GetPrincipalRanges(
            Polyline pl,
            Point2d origin,
            Vector2d major,
            Vector2d minor,
            out double minT,
            out double maxT,
            out double minS,
            out double maxS)
        {
            minT = double.PositiveInfinity;
            maxT = double.NegativeInfinity;
            minS = double.PositiveInfinity;
            maxS = double.NegativeInfinity;

            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var p = pl.GetPoint2dAt(i);
                var d = p - origin;
                double t = d.DotProduct(major);
                double s = d.DotProduct(minor);

                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
                if (s < minS) minS = s;
                if (s > maxS) maxS = s;
            }

            if (!IsFinite(minT) || !IsFinite(maxT))
            {
                minT = 0;
                maxT = 0;
            }

            if (!IsFinite(minS) || !IsFinite(maxS))
            {
                minS = 0;
                maxS = 0;
            }
        }

        private static bool TryCrossSectionWidth(Polyline corridor, Point2d center, Vector2d direction, double halfLength, out double width)
        {
            width = 0;

            try
            {
                var dir = direction;
                if (dir.Length < 1e-9) return false;
                dir = dir.GetNormal();

                // Build a long line segment through the corridor
                var p1 = new Point3d(center.X - dir.X * halfLength, center.Y - dir.Y * halfLength, corridor.Elevation);
                var p2 = new Point3d(center.X + dir.X * halfLength, center.Y + dir.Y * halfLength, corridor.Elevation);

                using (var line = new Line(p1, p2))
                {
                    var pts = new Point3dCollection();
                    corridor.IntersectWith(line, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);

                    if (pts.Count < 2)
                        return false;

                    // Deduplicate + project along direction (relative to center)
                    var proj = new List<double>(pts.Count);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var p = pts[i];
                        double s = (p.X - center.X) * dir.X + (p.Y - center.Y) * dir.Y;

                        bool dup = false;
                        for (int j = 0; j < proj.Count; j++)
                        {
                            if (Math.Abs(proj[j] - s) < 1e-6) { dup = true; break; }
                        }
                        if (!dup) proj.Add(s);
                    }

                    if (proj.Count < 2)
                        return false;

                    // Sort the projections
                    proj.Sort();

                    // If only two intersections, width = difference as before
                    if (proj.Count == 2)
                    {
                        width = Math.Abs(proj[1] - proj[0]);
                        return width > 1e-6;
                    }

                    // For 3+ intersections, compute candidate widths between adjacent pairs
                    double minSpan = double.MaxValue;
                    for (int i = 0; i < proj.Count - 1; i++)
                    {
                        double span = Math.Abs(proj[i + 1] - proj[i]);
                        if (span > 1e-6 && span < minSpan)
                            minSpan = span;
                    }

                    if (double.IsFinite(minSpan) && minSpan < double.MaxValue)
                    {
                        width = minSpan;
                        return true;
                    }

                    // Fallback: treat as failure
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }


        /// <summary>
        /// Returns a cloned closed boundary polyline for an imported disposition entity.
        /// Supports LWPOLYLINE and common Map 3D polygon entities (MPOLYGON / POLYLINE2D / POLYLINE3D) via explode.
        /// </summary>
        public static bool TryGetClosedBoundaryClone(Entity ent, out Polyline boundaryClone)
        {
            boundaryClone = null;

            if (ent == null)
                return false;

            if (ent is Polyline pl)
            {
                if (!pl.Closed || pl.NumberOfVertices < 3)
                    return false;

                boundaryClone = (Polyline)pl.Clone();
                return true;
            }

            // Map-imported polygons often arrive as MPOLYGON or old-style POLYLINE* entities.
            if (ent is Polyline2d || ent is Polyline3d || string.Equals(ent.GetType().Name, "MPolygon", StringComparison.OrdinalIgnoreCase))
            {
                var exploded = ExplodeToBestClosedPolyline(ent);
                if (exploded == null)
                    return false;

                boundaryClone = exploded;
                return true;
            }

            return false;
        }

        private static Polyline? ExplodeToBestClosedPolyline(Entity ent)
        {
            var col = new DBObjectCollection();
            try
            {
                ent.Explode(col);
            }
            catch
            {
                foreach (DBObject o in col) o.Dispose();
                return null;
            }

            Polyline best = null;
            double bestScore = -1.0;

            foreach (DBObject obj in col)
            {
                if (obj is Polyline p && p.Closed && p.NumberOfVertices >= 3)
                {
                    double score = 0.0;
                    try
                    {
                        var ext = p.GeometricExtents;
                        score = Math.Abs((ext.MaxPoint.X - ext.MinPoint.X) * (ext.MaxPoint.Y - ext.MinPoint.Y));
                    }
                    catch
                    {
                        score = 0.0;
                    }

                    if (best == null || score > bestScore)
                    {
                        best?.Dispose();
                        best = p;
                        bestScore = score;
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
                else
                {
                    obj.Dispose();
                }
            }

            if (best == null)
                return null;

            var clone = (Polyline)best.Clone();
            best.Dispose();
            return clone;
        }

    }
}
