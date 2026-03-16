using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionZeroCompanionProjection
    {
        public static bool TryProjectCompanionTarget(
            LineDistancePoint endpoint,
            LineDistancePoint otherEndpoint,
            LineDistancePoint ordinaryTarget,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters,
            double minExtendMeters,
            double maxExtendMeters,
            out LineDistancePoint correctionTarget)
        {
            correctionTarget = default;
            if (correctionZeroSegments == null || correctionZeroSegments.Count == 0)
            {
                return false;
            }

            var outwardX = endpoint.X - otherEndpoint.X;
            var outwardY = endpoint.Y - otherEndpoint.Y;
            var outwardLength = Math.Sqrt((outwardX * outwardX) + (outwardY * outwardY));
            if (outwardLength <= 1e-6)
            {
                return false;
            }

            var outwardUnitX = outwardX / outwardLength;
            var outwardUnitY = outwardY / outwardLength;
            var bestFound = false;
            var bestShift = double.MaxValue;
            var bestAlong = double.MaxValue;
            for (var i = 0; i < correctionZeroSegments.Count; i++)
            {
                var correction = correctionZeroSegments[i];
                if (!TryIntersectInfiniteLines(endpoint, otherEndpoint, correction.A, correction.B, out var candidate))
                {
                    continue;
                }

                var along = ((candidate.X - endpoint.X) * outwardUnitX) + ((candidate.Y - endpoint.Y) * outwardUnitY);
                if (along <= minExtendMeters || along > maxExtendMeters)
                {
                    continue;
                }

                var ordinaryOffset = Math.Abs(DistancePointToInfiniteLine(ordinaryTarget, correction.A, correction.B));
                if (Math.Abs(ordinaryOffset - expectedInsetMeters) > insetToleranceMeters)
                {
                    continue;
                }

                var shift = Distance(candidate, ordinaryTarget);
                if (Math.Abs(shift - expectedInsetMeters) > insetToleranceMeters)
                {
                    continue;
                }

                if (!bestFound ||
                    shift < bestShift - 1e-6 ||
                    (Math.Abs(shift - bestShift) <= 1e-6 && along < bestAlong - 1e-6))
                {
                    bestFound = true;
                    bestShift = shift;
                    bestAlong = along;
                    correctionTarget = candidate;
                }
            }

            return bestFound;
        }

        private static bool TryIntersectInfiniteLines(
            LineDistancePoint a0,
            LineDistancePoint a1,
            LineDistancePoint b0,
            LineDistancePoint b1,
            out LineDistancePoint intersection)
        {
            intersection = default;
            var dax = a1.X - a0.X;
            var day = a1.Y - a0.Y;
            var dbx = b1.X - b0.X;
            var dby = b1.Y - b0.Y;
            var denominator = (dax * dby) - (day * dbx);
            if (Math.Abs(denominator) <= 1e-9)
            {
                return false;
            }

            var diffX = b0.X - a0.X;
            var diffY = b0.Y - a0.Y;
            var t = ((diffX * dby) - (diffY * dbx)) / denominator;
            intersection = new LineDistancePoint(a0.X + (dax * t), a0.Y + (day * t));
            return true;
        }

        private static double DistancePointToInfiniteLine(LineDistancePoint point, LineDistancePoint lineA, LineDistancePoint lineB)
        {
            var dirX = lineB.X - lineA.X;
            var dirY = lineB.Y - lineA.Y;
            var length = Math.Sqrt((dirX * dirX) + (dirY * dirY));
            if (length <= 1e-6)
            {
                return Distance(point, lineA);
            }

            return ((point.X - lineA.X) * dirY - (point.Y - lineA.Y) * dirX) / length;
        }

        private static double Distance(LineDistancePoint left, LineDistancePoint right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
    }
}
