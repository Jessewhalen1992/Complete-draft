using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal static class BoundaryContainmentHelper
    {
        internal readonly record struct ExactContainmentEvaluation(
            bool BoundaryValid,
            bool CouldClassify,
            bool IsInside,
            bool IsOnBoundary,
            double BoundaryDistance,
            int SuccessfulRayCastCount,
            int OddRayCastCount,
            int EvenRayCastCount,
            int RawIntersectionCount,
            int UniqueIntersectionCount,
            double WinningRayYOffset,
            string Error);

        internal readonly record struct SampledContainmentEvaluation(
            bool BoundaryValid,
            bool IsInside,
            bool IsNearBoundary,
            bool RayCastingInside,
            bool WindingInside,
            double BoundaryDistance);

        public static bool IsInside(
            IReadOnlyList<Point3d> vertices,
            Point3d point,
            double boundaryEdgeTolerance,
            bool use3dDistanceForDegenerateSegments)
        {
            return EvaluateSampledContainment(
                vertices,
                point,
                boundaryEdgeTolerance,
                use3dDistanceForDegenerateSegments).IsInside;
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

        public static bool IsInsideExactPolyline(
            Polyline boundary,
            Point3d point,
            double boundaryEdgeTolerance,
            double rayYOffset)
        {
            return EvaluateExactPolylineContainment(
                boundary,
                point,
                boundaryEdgeTolerance,
                rayYOffset).IsInside;
        }

        public static ExactContainmentEvaluation EvaluateExactPolylineContainment(
            Polyline boundary,
            Point3d point,
            double boundaryEdgeTolerance,
            double rayYOffset)
        {
            if (boundary == null || !boundary.Closed || boundary.NumberOfVertices < 3)
            {
                return new ExactContainmentEvaluation(
                    BoundaryValid: false,
                    CouldClassify: false,
                    IsInside: false,
                    IsOnBoundary: false,
                    BoundaryDistance: double.NaN,
                    SuccessfulRayCastCount: 0,
                    OddRayCastCount: 0,
                    EvenRayCastCount: 0,
                    RawIntersectionCount: 0,
                    UniqueIntersectionCount: 0,
                    WinningRayYOffset: double.NaN,
                    Error: "Boundary must be a closed polyline with at least 3 vertices.");
            }

            var adjustedPoint = new Point3d(point.X, point.Y, boundary.Elevation);
            var boundaryDistance = DistanceToExactPolylineBoundary(boundary, adjustedPoint);
            if (boundaryDistance <= boundaryEdgeTolerance)
            {
                return new ExactContainmentEvaluation(
                    BoundaryValid: true,
                    CouldClassify: true,
                    IsInside: true,
                    IsOnBoundary: true,
                    BoundaryDistance: boundaryDistance,
                    SuccessfulRayCastCount: 0,
                    OddRayCastCount: 0,
                    EvenRayCastCount: 0,
                    RawIntersectionCount: 0,
                    UniqueIntersectionCount: 0,
                    WinningRayYOffset: 0.0,
                    Error: string.Empty);
            }

            var castResults = new List<(double Offset, int RawCount, int UniqueCount, bool IsInside)>();
            var errors = new List<string>();
            foreach (var candidateOffset in BuildCandidateRayOffsets(rayYOffset))
            {
                if (!TryCountUniqueIntersections(
                        boundary,
                        adjustedPoint,
                        boundaryEdgeTolerance,
                        candidateOffset,
                        out var rawCount,
                        out var uniqueCount,
                        out var error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        errors.Add(error);
                    }

                    continue;
                }

                castResults.Add((candidateOffset, rawCount, uniqueCount, (uniqueCount % 2) == 1));
            }

            if (castResults.Count == 0)
            {
                return new ExactContainmentEvaluation(
                    BoundaryValid: true,
                    CouldClassify: false,
                    IsInside: false,
                    IsOnBoundary: false,
                    BoundaryDistance: boundaryDistance,
                    SuccessfulRayCastCount: 0,
                    OddRayCastCount: 0,
                    EvenRayCastCount: 0,
                    RawIntersectionCount: 0,
                    UniqueIntersectionCount: 0,
                    WinningRayYOffset: double.NaN,
                    Error: errors.Count == 0
                        ? "Exact boundary ray casting failed."
                        : string.Join(" | ", errors));
            }

            var oddCount = castResults.Count(result => result.IsInside);
            var evenCount = castResults.Count - oddCount;
            var couldClassify = oddCount != evenCount;
            var finalInside = couldClassify && oddCount > evenCount;
            var winningParity = couldClassify ? finalInside : castResults[0].IsInside;
            var winningCast = castResults
                .Where(result => result.IsInside == winningParity)
                .OrderBy(result => Math.Abs(result.Offset))
                .ThenBy(result => result.UniqueCount)
                .FirstOrDefault();
            var errorText = errors.Count == 0
                ? (couldClassify ? string.Empty : "Exact boundary ray casts disagreed; using sampled fallback.")
                : string.Join(" | ", errors);

            return new ExactContainmentEvaluation(
                BoundaryValid: true,
                CouldClassify: couldClassify,
                IsInside: finalInside,
                IsOnBoundary: false,
                BoundaryDistance: boundaryDistance,
                SuccessfulRayCastCount: castResults.Count,
                OddRayCastCount: oddCount,
                EvenRayCastCount: evenCount,
                RawIntersectionCount: winningCast.RawCount,
                UniqueIntersectionCount: winningCast.UniqueCount,
                WinningRayYOffset: winningCast.Offset,
                Error: errorText);
        }

        public static SampledContainmentEvaluation EvaluateSampledContainment(
            IReadOnlyList<Point3d> vertices,
            Point3d point,
            double boundaryEdgeTolerance,
            bool use3dDistanceForDegenerateSegments)
        {
            if (vertices == null || vertices.Count < 3)
            {
                return new SampledContainmentEvaluation(
                    BoundaryValid: false,
                    IsInside: false,
                    IsNearBoundary: false,
                    RayCastingInside: false,
                    WindingInside: false,
                    BoundaryDistance: double.MaxValue);
            }

            var boundaryDistance = DistanceToBoundary(vertices, point, use3dDistanceForDegenerateSegments);
            var isNearBoundary = IsNearBoundary(vertices, point, boundaryEdgeTolerance, use3dDistanceForDegenerateSegments);
            var rayCastingInside = IsInsideByRayCasting(vertices, point);
            var windingInside = IsInsideByWindingNumber(vertices, point);
            return new SampledContainmentEvaluation(
                BoundaryValid: true,
                IsInside: isNearBoundary || rayCastingInside || windingInside,
                IsNearBoundary: isNearBoundary,
                RayCastingInside: rayCastingInside,
                WindingInside: windingInside,
                BoundaryDistance: boundaryDistance);
        }

        public static double DistanceToExactPolylineBoundary(Polyline boundary, Point3d point)
        {
            if (boundary == null)
            {
                return double.MaxValue;
            }

            try
            {
                var adjustedPoint = new Point3d(point.X, point.Y, boundary.Elevation);
                var closestPoint = boundary.GetClosestPointTo(adjustedPoint, extend: false);
                var deltaX = adjustedPoint.X - closestPoint.X;
                var deltaY = adjustedPoint.Y - closestPoint.Y;
                return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            }
            catch
            {
                return double.MaxValue;
            }
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

        private static IReadOnlyList<double> BuildCandidateRayOffsets(double rayYOffset)
        {
            var baseOffset = Math.Abs(rayYOffset) > 1.0e-6 ? rayYOffset : 0.01;
            var offsets = new List<double>(5);
            TryAddOffset(offsets, baseOffset);
            TryAddOffset(offsets, -baseOffset);
            TryAddOffset(offsets, baseOffset * 5.0);
            TryAddOffset(offsets, -baseOffset * 5.0);
            TryAddOffset(offsets, 0.0);
            return offsets;
        }

        private static void TryAddOffset(List<double> offsets, double candidate)
        {
            for (var index = 0; index < offsets.Count; index++)
            {
                if (Math.Abs(offsets[index] - candidate) <= 1.0e-9)
                {
                    return;
                }
            }

            offsets.Add(candidate);
        }

        private static bool TryCountUniqueIntersections(
            Polyline boundary,
            Point3d adjustedPoint,
            double boundaryEdgeTolerance,
            double rayYOffset,
            out int rawCount,
            out int uniqueCount,
            out string error)
        {
            rawCount = 0;
            uniqueCount = 0;
            error = string.Empty;

            try
            {
                var extents = boundary.GeometricExtents;
                var rayY = adjustedPoint.Y + rayYOffset;
                var rightMargin = Math.Max(10.0, (extents.MaxPoint.X - extents.MinPoint.X) + 10.0);
                var rayStart = new Point3d(adjustedPoint.X, rayY, adjustedPoint.Z);
                var rayEnd = new Point3d(Math.Max(adjustedPoint.X, extents.MaxPoint.X) + rightMargin, rayY, adjustedPoint.Z);

                using var ray = new Line(rayStart, rayEnd);
                var intersections = new Point3dCollection();
                boundary.IntersectWith(ray, Intersect.OnBothOperands, intersections, IntPtr.Zero, IntPtr.Zero);
                rawCount = intersections.Count;

                var uniqueHits = new List<Point3d>(intersections.Count);
                var mergeTolerance = Math.Max(boundaryEdgeTolerance * 0.25, Math.Abs(rayYOffset) * 4.0);
                for (var i = 0; i < intersections.Count; i++)
                {
                    var hit = intersections[i];
                    if (hit.X + mergeTolerance < adjustedPoint.X)
                    {
                        continue;
                    }

                    var isDuplicate = false;
                    for (var j = 0; j < uniqueHits.Count; j++)
                    {
                        if (Math.Abs(uniqueHits[j].X - hit.X) <= mergeTolerance &&
                            Math.Abs(uniqueHits[j].Y - hit.Y) <= mergeTolerance)
                        {
                            isDuplicate = true;
                            break;
                        }
                    }

                    if (!isDuplicate)
                    {
                        uniqueHits.Add(hit);
                    }
                }

                uniqueCount = uniqueHits.Count;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
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
