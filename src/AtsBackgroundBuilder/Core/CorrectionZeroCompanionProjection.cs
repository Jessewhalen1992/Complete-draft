using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionZeroCompanionProjection
    {
        private const double CandidateComparisonTolerance = 1e-6;

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

            if (!TryGetOutwardUnitVector(endpoint, otherEndpoint, out var outwardUnitX, out var outwardUnitY))
            {
                return false;
            }

            var bestFound = false;
            var bestCandidate = default(CompanionProjectionCandidate);
            for (var i = 0; i < correctionZeroSegments.Count; i++)
            {
                var correction = correctionZeroSegments[i];
                if (!TryBuildCandidate(
                        endpoint,
                        otherEndpoint,
                        ordinaryTarget,
                        correction.A,
                        correction.B,
                        outwardUnitX,
                        outwardUnitY,
                        expectedInsetMeters,
                        insetToleranceMeters,
                        minExtendMeters,
                        maxExtendMeters,
                        out var candidate))
                {
                    continue;
                }

                if (!bestFound || candidate.IsBetterThan(bestCandidate))
                {
                    bestFound = true;
                    bestCandidate = candidate;
                }
            }

            correctionTarget = bestCandidate.Target;
            return bestFound;
        }

        private static bool TryGetOutwardUnitVector(
            LineDistancePoint endpoint,
            LineDistancePoint otherEndpoint,
            out double outwardUnitX,
            out double outwardUnitY)
        {
            outwardUnitX = endpoint.X - otherEndpoint.X;
            outwardUnitY = endpoint.Y - otherEndpoint.Y;
            var outwardLength = Math.Sqrt((outwardUnitX * outwardUnitX) + (outwardUnitY * outwardUnitY));
            if (outwardLength <= CandidateComparisonTolerance)
            {
                outwardUnitX = 0.0;
                outwardUnitY = 0.0;
                return false;
            }

            outwardUnitX /= outwardLength;
            outwardUnitY /= outwardLength;
            return true;
        }

        private static bool TryBuildCandidate(
            LineDistancePoint endpoint,
            LineDistancePoint otherEndpoint,
            LineDistancePoint ordinaryTarget,
            LineDistancePoint correctionA,
            LineDistancePoint correctionB,
            double outwardUnitX,
            double outwardUnitY,
            double expectedInsetMeters,
            double insetToleranceMeters,
            double minExtendMeters,
            double maxExtendMeters,
            out CompanionProjectionCandidate candidate)
        {
            candidate = default;
            if (!TryIntersectInfiniteLines(endpoint, otherEndpoint, correctionA, correctionB, out var projected))
            {
                return false;
            }

            var along = ((projected.X - endpoint.X) * outwardUnitX) + ((projected.Y - endpoint.Y) * outwardUnitY);
            if (along <= minExtendMeters || along > maxExtendMeters)
            {
                return false;
            }

            var ordinaryOffset = Math.Abs(DistancePointToInfiniteLine(ordinaryTarget, correctionA, correctionB));
            var ordinaryOffsetDelta = Math.Abs(ordinaryOffset - expectedInsetMeters);
            if (ordinaryOffsetDelta > insetToleranceMeters)
            {
                return false;
            }

            var shift = Distance(projected, ordinaryTarget);
            var shiftDelta = Math.Abs(shift - expectedInsetMeters);
            if (shiftDelta > insetToleranceMeters)
            {
                return false;
            }

            var segmentGap = DistancePointToSegment(projected, correctionA, correctionB);
            candidate = new CompanionProjectionCandidate(
                projected,
                segmentGap,
                shiftDelta,
                ordinaryOffsetDelta,
                along);
            return true;
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

        private static double DistancePointToSegment(LineDistancePoint point, LineDistancePoint segmentA, LineDistancePoint segmentB)
        {
            var dirX = segmentB.X - segmentA.X;
            var dirY = segmentB.Y - segmentA.Y;
            var lengthSquared = (dirX * dirX) + (dirY * dirY);
            if (lengthSquared <= 1e-12)
            {
                return Distance(point, segmentA);
            }

            var projection =
                (((point.X - segmentA.X) * dirX) + ((point.Y - segmentA.Y) * dirY)) / lengthSquared;
            if (projection <= 0.0)
            {
                return Distance(point, segmentA);
            }

            if (projection >= 1.0)
            {
                return Distance(point, segmentB);
            }

            var closest = new LineDistancePoint(
                segmentA.X + (dirX * projection),
                segmentA.Y + (dirY * projection));
            return Distance(point, closest);
        }

        private static double Distance(LineDistancePoint left, LineDistancePoint right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private readonly struct CompanionProjectionCandidate
        {
            public CompanionProjectionCandidate(
                LineDistancePoint target,
                double segmentGap,
                double shiftDelta,
                double ordinaryOffsetDelta,
                double along)
            {
                Target = target;
                SegmentGap = segmentGap;
                ShiftDelta = shiftDelta;
                OrdinaryOffsetDelta = ordinaryOffsetDelta;
                Along = along;
            }

            public LineDistancePoint Target { get; }

            private double SegmentGap { get; }

            private double ShiftDelta { get; }

            private double OrdinaryOffsetDelta { get; }

            private double Along { get; }

            public bool IsBetterThan(CompanionProjectionCandidate other)
            {
                return SegmentGap < other.SegmentGap - CandidateComparisonTolerance ||
                       (Math.Abs(SegmentGap - other.SegmentGap) <= CandidateComparisonTolerance &&
                        (ShiftDelta < other.ShiftDelta - CandidateComparisonTolerance ||
                         (Math.Abs(ShiftDelta - other.ShiftDelta) <= CandidateComparisonTolerance &&
                          (OrdinaryOffsetDelta < other.OrdinaryOffsetDelta - CandidateComparisonTolerance ||
                           (Math.Abs(OrdinaryOffsetDelta - other.OrdinaryOffsetDelta) <= CandidateComparisonTolerance &&
                            Along < other.Along - CandidateComparisonTolerance)))));
            }
        }
    }
}
