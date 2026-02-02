using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public static class GeometryUtils
    {
        public static bool PointInPolyline(Polyline polyline, Point2d point)
        {
            var inside = false;
            var count = polyline.NumberOfVertices;
            if (count < 3)
            {
                return false;
            }

            var j = count - 1;
            for (var i = 0; i < count; i++)
            {
                var pi = polyline.GetPoint2dAt(i);
                var pj = polyline.GetPoint2dAt(j);
                var intersect = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                                (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y + 1e-9) + pi.X);
                if (intersect)
                {
                    inside = !inside;
                }
                j = i;
            }

            return inside;
        }

        public static Point2d GetSafeInteriorPoint(Polyline polyline)
        {
            var centroid = GetCentroid(polyline);
            if (PointInPolyline(polyline, centroid))
            {
                return centroid;
            }

            var midpoint = polyline.GetPointAtDist(polyline.Length / 2.0);
            var fallback = new Point2d(midpoint.X, midpoint.Y);
            if (PointInPolyline(polyline, fallback))
            {
                return fallback;
            }

            var extents = polyline.GeometricExtents;
            var nudge = new Point2d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
            return nudge;
        }

        public static Point2d GetCentroid(Polyline polyline)
        {
            var count = polyline.NumberOfVertices;
            if (count < 3)
            {
                var first = polyline.GetPoint2dAt(0);
                return new Point2d(first.X, first.Y);
            }

            double accumulatedArea = 0.0;
            double centerX = 0.0;
            double centerY = 0.0;

            for (var i = 0; i < count; i++)
            {
                var p0 = polyline.GetPoint2dAt(i);
                var p1 = polyline.GetPoint2dAt((i + 1) % count);
                var cross = p0.X * p1.Y - p1.X * p0.Y;
                accumulatedArea += cross;
                centerX += (p0.X + p1.X) * cross;
                centerY += (p0.Y + p1.Y) * cross;
            }

            if (Math.Abs(accumulatedArea) < 1e-9)
            {
                var fallback = polyline.GetPoint2dAt(0);
                return new Point2d(fallback.X, fallback.Y);
            }

            accumulatedArea *= 0.5;
            centerX /= (6.0 * accumulatedArea);
            centerY /= (6.0 * accumulatedArea);
            return new Point2d(centerX, centerY);
        }

        public static bool TryIntersectRegions(Polyline subject, Polyline clip, out List<Region> regions)
        {
            regions = new List<Region>();
            try
            {
                var subjectRegion = CreateRegion(subject);
                var clipRegion = CreateRegion(clip);
                if (subjectRegion == null || clipRegion == null)
                {
                    subjectRegion?.Dispose();
                    clipRegion?.Dispose();
                    return false;
                }

                using (clipRegion)
                {
                    subjectRegion.BooleanOperation(BooleanOperationType.BoolIntersect, clipRegion);
                }

                regions.Add(subjectRegion);

                return regions.Count > 0;
            }
            catch
            {
                foreach (var region in regions)
                {
                    region.Dispose();
                }
                regions.Clear();
                return false;
            }
        }

        private static Region? CreateRegion(Polyline polyline)
        {
            var curves = new DBObjectCollection();
            curves.Add(polyline);
            var regions = Region.CreateFromCurves(curves);
            if (regions.Count == 0)
            {
                return null;
            }

            return regions[0] as Region;
        }

        public static bool ExtentsIntersect(Extents2d a, Extents2d b)
        {
            return !(a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
                     a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y);
        }

        public static IEnumerable<Point2d> GetSpiralOffsets(Point2d origin, double step, int count)
        {
            yield return origin;
            var radius = step;
            var generated = 1;
            while (generated < count)
            {
                for (var dx = -1; dx <= 1 && generated < count; dx++)
                {
                    for (var dy = -1; dy <= 1 && generated < count; dy++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        yield return new Point2d(origin.X + dx * radius, origin.Y + dy * radius);
                        generated++;
                    }
                }
                radius += step;
            }
        }
    }
}

