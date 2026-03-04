using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal static class BoundaryContainmentHelper
    {
        public static bool IsInside(
            IReadOnlyList<Point3d> vertices,
            Point3d point,
            double boundaryEdgeTolerance,
            bool use3dDistanceForDegenerateSegments)
        {
            if (vertices == null || vertices.Count < 3)
            {
                return false;
            }

            if (IsNearBoundary(vertices, point, boundaryEdgeTolerance, use3dDistanceForDegenerateSegments))
            {
                return true;
            }

            return IsInsideByRayCasting(vertices, point) || IsInsideByWindingNumber(vertices, point);
        }

        public static double DistanceToBoundary(
            IReadOnlyList<Point3d> vertices,
            Point3d point,
            bool use3dDistanceForDegenerateSegments)
        {
            if (vertices == null || vertices.Count < 2)
            {
                return double.MaxValue;
            }

            var minimumDistanceSq = double.MaxValue;
            for (var index = 0; index < vertices.Count; index++)
            {
                var start = vertices[index];
                var end = vertices[(index + 1) % vertices.Count];
                var distanceSq = DistanceSqToSegment(point, start, end, use3dDistanceForDegenerateSegments);
                if (distanceSq < minimumDistanceSq)
                {
                    minimumDistanceSq = distanceSq;
                }
            }

            return Math.Sqrt(minimumDistanceSq);
        }

        private static bool IsInsideByRayCasting(IReadOnlyList<Point3d> vertices, Point3d point)
        {
            var inside = false;
            var previous = vertices[vertices.Count - 1];
            for (var index = 0; index < vertices.Count; index++)
            {
                var current = vertices[index];
                var y1 = previous.Y;
                var y2 = current.Y;
                if ((y1 > point.Y) != (y2 > point.Y))
                {
                    var xIntersection = ((current.X - previous.X) * (point.Y - y1) / (y2 - y1)) + previous.X;
                    if (point.X < xIntersection)
                    {
                        inside = !inside;
                    }
                }

                previous = current;
            }

            return inside;
        }

        private static bool IsInsideByWindingNumber(IReadOnlyList<Point3d> vertices, Point3d point)
        {
            var windingNumber = 0;
            for (var index = 0; index < vertices.Count; index++)
            {
                var start = vertices[index];
                var end = vertices[(index + 1) % vertices.Count];

                if (start.Y <= point.Y)
                {
                    if (end.Y > point.Y && IsLeft(start, end, point) > 0)
                    {
                        windingNumber++;
                    }
                }
                else if (end.Y <= point.Y && IsLeft(start, end, point) < 0)
                {
                    windingNumber--;
                }
            }

            return windingNumber != 0;
        }

        private static double IsLeft(Point3d start, Point3d end, Point3d point)
        {
            return ((end.X - start.X) * (point.Y - start.Y))
                   - ((point.X - start.X) * (end.Y - start.Y));
        }

        private static bool IsNearBoundary(
            IReadOnlyList<Point3d> vertices,
            Point3d point,
            double tolerance,
            bool use3dDistanceForDegenerateSegments)
        {
            if (vertices.Count < 2)
            {
                return false;
            }

            var toleranceSq = tolerance * tolerance;
            for (var index = 0; index < vertices.Count; index++)
            {
                var start = vertices[index];
                var end = vertices[(index + 1) % vertices.Count];
                if (DistanceSqToSegment(point, start, end, use3dDistanceForDegenerateSegments) <= toleranceSq)
                {
                    return true;
                }
            }

            return false;
        }

        private static double DistanceSqToSegment(
            Point3d point,
            Point3d start,
            Point3d end,
            bool use3dDistanceForDegenerateSegments)
        {
            var segmentX = end.X - start.X;
            var segmentY = end.Y - start.Y;
            var segmentLenSq = (segmentX * segmentX) + (segmentY * segmentY);
            if (segmentLenSq <= 0.0)
            {
                if (use3dDistanceForDegenerateSegments)
                {
                    var distance = point.DistanceTo(start);
                    return distance * distance;
                }

                var dx = point.X - start.X;
                var dy = point.Y - start.Y;
                return (dx * dx) + (dy * dy);
            }

            var deltaX = point.X - start.X;
            var deltaY = point.Y - start.Y;
            var projection = (deltaX * segmentX) + (deltaY * segmentY);
            var clampedT = Math.Max(0.0, Math.Min(1.0, projection / segmentLenSq));

            var closestX = start.X + (clampedT * segmentX);
            var closestY = start.Y + (clampedT * segmentY);
            var distanceX = point.X - closestX;
            var distanceY = point.Y - closestY;
            return (distanceX * distanceX) + (distanceY * distanceY);
        }
    }
}
