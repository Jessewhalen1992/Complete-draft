using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionInsetGhostRowClassifier
    {
        private const double LengthTolerance = 1e-6;
        private const double DirectionDotMin = 0.995;

        public static IReadOnlyCollection<int> FindGhostChainIndices(
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> ordinarySegments,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters = 1.0,
            double minOverlapMeters = 40.0,
            double endpointTouchToleranceMeters = 0.75)
        {
            if (ordinarySegments == null || ordinarySegments.Count == 0)
            {
                return Array.Empty<int>();
            }

            var matchInfos = new GhostMatchInfo?[ordinarySegments.Count];
            var ghostIndices = new HashSet<int>();
            var workQueue = new Queue<int>();
            for (var i = 0; i < ordinarySegments.Count; i++)
            {
                if (!TryGetRequiredOverlapGhostMatch(
                        ordinarySegments[i],
                        correctionZeroSegments,
                        expectedInsetMeters,
                        insetToleranceMeters,
                        minOverlapMeters,
                        out var seedMatch))
                {
                    continue;
                }

                matchInfos[i] = seedMatch;
                ghostIndices.Add(i);
                workQueue.Enqueue(i);
            }

            if (ghostIndices.Count == 0)
            {
                return Array.Empty<int>();
            }

            while (workQueue.Count > 0)
            {
                var currentIndex = workQueue.Dequeue();
                var currentMatch = matchInfos[currentIndex];
                if (!currentMatch.HasValue)
                {
                    continue;
                }

                var currentSegment = ordinarySegments[currentIndex];
                for (var candidateIndex = 0; candidateIndex < ordinarySegments.Count; candidateIndex++)
                {
                    if (ghostIndices.Contains(candidateIndex))
                    {
                        continue;
                    }

                    var candidateSegment = ordinarySegments[candidateIndex];
                    if (!TryGetConnectedGhostChainMatch(
                            currentSegment,
                            candidateSegment,
                            correctionZeroSegments,
                            expectedInsetMeters,
                            insetToleranceMeters,
                            endpointTouchToleranceMeters,
                            currentMatch.Value,
                            matchInfos[candidateIndex],
                            out var candidateMatch))
                    {
                        continue;
                    }

                    matchInfos[candidateIndex] = candidateMatch;
                    ghostIndices.Add(candidateIndex);
                    workQueue.Enqueue(candidateIndex);
                }
            }

            return ghostIndices.Count == 0
                ? Array.Empty<int>()
                : new List<int>(ghostIndices);
        }

        public static IReadOnlyCollection<TId> FindGhostChainIds<TId>(
            IReadOnlyList<(TId Id, LineDistancePoint A, LineDistancePoint B)> ordinarySegments,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters = 1.0,
            double minOverlapMeters = 40.0,
            double endpointTouchToleranceMeters = 0.75)
            where TId : notnull
        {
            if (ordinarySegments == null || ordinarySegments.Count == 0)
            {
                return Array.Empty<TId>();
            }

            var indexedSegments = new List<(LineDistancePoint A, LineDistancePoint B)>(ordinarySegments.Count);
            for (var i = 0; i < ordinarySegments.Count; i++)
            {
                var segment = ordinarySegments[i];
                indexedSegments.Add((segment.A, segment.B));
            }

            var ghostIndices = FindGhostChainIndices(
                indexedSegments,
                correctionZeroSegments,
                expectedInsetMeters,
                insetToleranceMeters,
                minOverlapMeters,
                endpointTouchToleranceMeters);
            if (ghostIndices.Count == 0)
            {
                return Array.Empty<TId>();
            }

            var ghostIds = new HashSet<TId>();
            foreach (var ghostIndex in ghostIndices)
            {
                ghostIds.Add(ordinarySegments[ghostIndex].Id);
            }

            return ghostIds.Count == 0
                ? Array.Empty<TId>()
                : new List<TId>(ghostIds);
        }

        public static bool HasInsetLikeOffset(
            LineDistancePoint sourceA,
            LineDistancePoint sourceB,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters = 1.0)
        {
            return TryGetInsetMatchInfo(
                sourceA,
                sourceB,
                correctionZeroSegments,
                expectedInsetMeters,
                insetToleranceMeters,
                requireOverlap: false,
                minOverlapMeters: 0.0,
                out _);
        }

        public static bool IsGhostRow(
            LineDistancePoint sourceA,
            LineDistancePoint sourceB,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters = 1.0,
            double minOverlapMeters = 40.0)
        {
            return TryGetInsetMatchInfo(
                sourceA,
                sourceB,
                correctionZeroSegments,
                expectedInsetMeters,
                insetToleranceMeters,
                requireOverlap: true,
                minOverlapMeters: minOverlapMeters,
                out _);
        }

        private static bool TryGetInsetMatchInfo(
            LineDistancePoint sourceA,
            LineDistancePoint sourceB,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters,
            bool requireOverlap,
            double minOverlapMeters,
            out GhostMatchInfo matchInfo)
        {
            matchInfo = default;
            if (correctionZeroSegments == null || correctionZeroSegments.Count == 0)
            {
                return false;
            }

            if (!TryCreateSegmentInfo(sourceA, sourceB, out var source))
            {
                return false;
            }

            if (!TryFindBestInsetMatchCandidate(
                    source,
                    correctionZeroSegments,
                    expectedInsetMeters,
                    insetToleranceMeters,
                    requireOverlap,
                    minOverlapMeters,
                    out var bestCandidate))
            {
                return false;
            }

            matchInfo = bestCandidate.MatchInfo;
            return true;
        }

        private static bool TryFindBestInsetMatchCandidate(
            SegmentInfo source,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters,
            bool requireOverlap,
            double minOverlapMeters,
            out InsetMatchCandidate bestCandidate)
        {
            bestCandidate = default;
            var found = false;
            for (var i = 0; i < correctionZeroSegments.Count; i++)
            {
                var correction = correctionZeroSegments[i];
                if (!TryCreateSegmentInfo(correction.A, correction.B, out var correctionInfo))
                {
                    continue;
                }

                if (!TryBuildInsetMatchCandidate(
                        source,
                        correctionInfo,
                        expectedInsetMeters,
                        insetToleranceMeters,
                        requireOverlap,
                        minOverlapMeters,
                        out var candidate))
                {
                    continue;
                }

                if (!found || candidate.IsBetterThan(bestCandidate))
                {
                    bestCandidate = candidate;
                    found = true;
                }
            }

            return found;
        }

        private static bool TryGetRequiredOverlapGhostMatch(
            (LineDistancePoint A, LineDistancePoint B) segment,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters,
            double minOverlapMeters,
            out GhostMatchInfo matchInfo)
        {
            return TryGetInsetMatchInfo(
                segment.A,
                segment.B,
                correctionZeroSegments,
                expectedInsetMeters,
                insetToleranceMeters,
                requireOverlap: true,
                minOverlapMeters,
                out matchInfo);
        }

        private static bool TryGetOptionalOverlapGhostMatch(
            (LineDistancePoint A, LineDistancePoint B) segment,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters,
            GhostMatchInfo? existingMatch,
            out GhostMatchInfo matchInfo)
        {
            if (existingMatch.HasValue)
            {
                matchInfo = existingMatch.Value;
                return true;
            }

            return TryGetInsetMatchInfo(
                segment.A,
                segment.B,
                correctionZeroSegments,
                expectedInsetMeters,
                insetToleranceMeters,
                requireOverlap: false,
                minOverlapMeters: 0.0,
                out matchInfo);
        }

        private static bool TryGetConnectedGhostChainMatch(
            (LineDistancePoint A, LineDistancePoint B) currentSegment,
            (LineDistancePoint A, LineDistancePoint B) candidateSegment,
            IReadOnlyList<(LineDistancePoint A, LineDistancePoint B)> correctionZeroSegments,
            double expectedInsetMeters,
            double insetToleranceMeters,
            double endpointTouchToleranceMeters,
            GhostMatchInfo currentMatch,
            GhostMatchInfo? existingMatch,
            out GhostMatchInfo candidateMatch)
        {
            candidateMatch = default;
            if (!TouchesAtEndpoint(currentSegment, candidateSegment, endpointTouchToleranceMeters))
            {
                return false;
            }

            if (!TryGetOptionalOverlapGhostMatch(
                    candidateSegment,
                    correctionZeroSegments,
                    expectedInsetMeters,
                    insetToleranceMeters,
                    existingMatch,
                    out candidateMatch))
            {
                return false;
            }

            return BelongsToSameGhostInsetFamily(currentMatch, candidateMatch, insetToleranceMeters);
        }

        private static bool TryCreateSegmentInfo(
            LineDistancePoint a,
            LineDistancePoint b,
            out SegmentInfo segmentInfo)
        {
            segmentInfo = default;
            var vectorX = b.X - a.X;
            var vectorY = b.Y - a.Y;
            var length = Math.Sqrt((vectorX * vectorX) + (vectorY * vectorY));
            if (length <= LengthTolerance)
            {
                return false;
            }

            segmentInfo = new SegmentInfo(
                a,
                b,
                length,
                vectorX / length,
                vectorY / length,
                Midpoint(a, b));
            return true;
        }

        private static bool TryBuildInsetMatchCandidate(
            SegmentInfo source,
            SegmentInfo correction,
            double expectedInsetMeters,
            double insetToleranceMeters,
            bool requireOverlap,
            double minOverlapMeters,
            out InsetMatchCandidate candidate)
        {
            candidate = default;
            if (!HasCompatibleDirection(source, correction))
            {
                return false;
            }

            if (requireOverlap &&
                !HasRequiredProjectedOverlap(source, correction, minOverlapMeters))
            {
                return false;
            }

            if (!TryGetInsetError(
                    source.Midpoint,
                    correction.A,
                    correction.B,
                    expectedInsetMeters,
                    insetToleranceMeters,
                    out var signedSourceToCorrection,
                    out var sourceInsetError))
            {
                return false;
            }

            if (!TryGetInsetError(
                    correction.Midpoint,
                    source.A,
                    source.B,
                    expectedInsetMeters,
                    insetToleranceMeters,
                    out _,
                    out var correctionInsetError))
            {
                return false;
            }

            candidate = new InsetMatchCandidate(
                new GhostMatchInfo(signedSourceToCorrection),
                sourceInsetError + correctionInsetError);
            return true;
        }

        private static bool HasCompatibleDirection(SegmentInfo source, SegmentInfo correction)
        {
            var dot = (source.UnitX * correction.UnitX) + (source.UnitY * correction.UnitY);
            return Math.Abs(dot) >= DirectionDotMin;
        }

        private static bool HasRequiredProjectedOverlap(
            SegmentInfo source,
            SegmentInfo correction,
            double minOverlapMeters)
        {
            var overlap = ProjectedOverlap(source.A, source.B, correction.A, correction.B);
            var minRequiredOverlap = Math.Max(minOverlapMeters, Math.Min(source.Length, correction.Length) * 0.25);
            return overlap >= minRequiredOverlap;
        }

        private static bool TryGetInsetError(
            LineDistancePoint point,
            LineDistancePoint lineA,
            LineDistancePoint lineB,
            double expectedInsetMeters,
            double insetToleranceMeters,
            out double signedDistance,
            out double insetError)
        {
            signedDistance = DistancePointToInfiniteLine(point, lineA, lineB);
            insetError = Math.Abs(Math.Abs(signedDistance) - expectedInsetMeters);
            return insetError <= insetToleranceMeters;
        }

        private static LineDistancePoint Midpoint(LineDistancePoint a, LineDistancePoint b)
        {
            return new LineDistancePoint((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        }

        private static double ProjectedOverlap(LineDistancePoint a0, LineDistancePoint a1, LineDistancePoint b0, LineDistancePoint b1)
        {
            var dax = a1.X - a0.X;
            var day = a1.Y - a0.Y;
            var lenA = Math.Sqrt((dax * dax) + (day * day));
            if (lenA <= LengthTolerance)
            {
                return double.NegativeInfinity;
            }

            var uax = dax / lenA;
            var uay = day / lenA;
            var aMin = 0.0;
            var aMax = lenA;
            var bS0 = ((b0.X - a0.X) * uax) + ((b0.Y - a0.Y) * uay);
            var bS1 = ((b1.X - a0.X) * uax) + ((b1.Y - a0.Y) * uay);
            var bMin = Math.Min(bS0, bS1);
            var bMax = Math.Max(bS0, bS1);
            return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
        }

        private static double DistancePointToInfiniteLine(LineDistancePoint point, LineDistancePoint lineA, LineDistancePoint lineB)
        {
            var dirX = lineB.X - lineA.X;
            var dirY = lineB.Y - lineA.Y;
            var len = Math.Sqrt((dirX * dirX) + (dirY * dirY));
            if (len <= LengthTolerance)
            {
                var dx = point.X - lineA.X;
                var dy = point.Y - lineA.Y;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }

            return ((point.X - lineA.X) * dirY - (point.Y - lineA.Y) * dirX) / len;
        }

        private static bool TouchesAtEndpoint(
            (LineDistancePoint A, LineDistancePoint B) left,
            (LineDistancePoint A, LineDistancePoint B) right,
            double endpointTouchToleranceMeters)
        {
            return Distance(left.A, right.A) <= endpointTouchToleranceMeters ||
                   Distance(left.A, right.B) <= endpointTouchToleranceMeters ||
                   Distance(left.B, right.A) <= endpointTouchToleranceMeters ||
                   Distance(left.B, right.B) <= endpointTouchToleranceMeters;
        }

        private static bool BelongsToSameGhostInsetFamily(
            GhostMatchInfo left,
            GhostMatchInfo right,
            double insetToleranceMeters)
        {
            if ((left.SignedInsetMeters * right.SignedInsetMeters) < 0.0)
            {
                return false;
            }

            return Math.Abs(Math.Abs(left.SignedInsetMeters) - Math.Abs(right.SignedInsetMeters)) <= insetToleranceMeters;
        }

        private static double Distance(LineDistancePoint left, LineDistancePoint right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private readonly struct GhostMatchInfo
        {
            public GhostMatchInfo(double signedInsetMeters)
            {
                SignedInsetMeters = signedInsetMeters;
            }

            public double SignedInsetMeters { get; }
        }

        private readonly struct SegmentInfo
        {
            public SegmentInfo(
                LineDistancePoint a,
                LineDistancePoint b,
                double length,
                double unitX,
                double unitY,
                LineDistancePoint midpoint)
            {
                A = a;
                B = b;
                Length = length;
                UnitX = unitX;
                UnitY = unitY;
                Midpoint = midpoint;
            }

            public LineDistancePoint A { get; }

            public LineDistancePoint B { get; }

            public double Length { get; }

            public double UnitX { get; }

            public double UnitY { get; }

            public LineDistancePoint Midpoint { get; }
        }

        private readonly struct InsetMatchCandidate
        {
            public InsetMatchCandidate(GhostMatchInfo matchInfo, double combinedInsetError)
            {
                MatchInfo = matchInfo;
                CombinedInsetError = combinedInsetError;
            }

            public GhostMatchInfo MatchInfo { get; }

            private double CombinedInsetError { get; }

            public bool IsBetterThan(InsetMatchCandidate other)
            {
                return CombinedInsetError < other.CombinedInsetError;
            }
        }
    }
}
