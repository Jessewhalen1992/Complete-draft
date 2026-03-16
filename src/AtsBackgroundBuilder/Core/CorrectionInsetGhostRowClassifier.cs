using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionInsetGhostRowClassifier
    {
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
                var segment = ordinarySegments[i];
                if (!TryGetInsetMatchInfo(
                        segment.A,
                        segment.B,
                        correctionZeroSegments,
                        expectedInsetMeters,
                        insetToleranceMeters,
                        requireOverlap: true,
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
                    if (!TouchesAtEndpoint(currentSegment, candidateSegment, endpointTouchToleranceMeters))
                    {
                        continue;
                    }

                    var candidateMatch = matchInfos[candidateIndex];
                    GhostMatchInfo resolvedCandidateMatch = default;
                    if (!candidateMatch.HasValue &&
                        !TryGetInsetMatchInfo(
                            candidateSegment.A,
                            candidateSegment.B,
                            correctionZeroSegments,
                            expectedInsetMeters,
                            insetToleranceMeters,
                            requireOverlap: false,
                            minOverlapMeters: 0.0,
                            out resolvedCandidateMatch))
                    {
                        continue;
                    }

                    if (!candidateMatch.HasValue)
                    {
                        candidateMatch = resolvedCandidateMatch;
                        matchInfos[candidateIndex] = resolvedCandidateMatch;
                    }

                    if (!BelongsToSameGhostInsetFamily(currentMatch.Value, candidateMatch.Value, insetToleranceMeters))
                    {
                        continue;
                    }

                    ghostIndices.Add(candidateIndex);
                    workQueue.Enqueue(candidateIndex);
                }
            }

            return ghostIndices.Count == 0
                ? Array.Empty<int>()
                : new List<int>(ghostIndices);
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

            var sourceVecX = sourceB.X - sourceA.X;
            var sourceVecY = sourceB.Y - sourceA.Y;
            var sourceLen = Math.Sqrt((sourceVecX * sourceVecX) + (sourceVecY * sourceVecY));
            if (sourceLen <= 1e-6)
            {
                return false;
            }

            var sourceUnitX = sourceVecX / sourceLen;
            var sourceUnitY = sourceVecY / sourceLen;
            var sourceMid = Midpoint(sourceA, sourceB);
            const double directionDotMin = 0.995;
            var found = false;
            var bestInsetError = double.MaxValue;
            GhostMatchInfo bestMatch = default;
            for (var i = 0; i < correctionZeroSegments.Count; i++)
            {
                var correction = correctionZeroSegments[i];
                var correctionVecX = correction.B.X - correction.A.X;
                var correctionVecY = correction.B.Y - correction.A.Y;
                var correctionLen = Math.Sqrt((correctionVecX * correctionVecX) + (correctionVecY * correctionVecY));
                if (correctionLen <= 1e-6)
                {
                    continue;
                }

                var correctionUnitX = correctionVecX / correctionLen;
                var correctionUnitY = correctionVecY / correctionLen;
                var dot = (sourceUnitX * correctionUnitX) + (sourceUnitY * correctionUnitY);
                if (Math.Abs(dot) < directionDotMin)
                {
                    continue;
                }

                if (requireOverlap)
                {
                    var overlap = ProjectedOverlap(sourceA, sourceB, correction.A, correction.B);
                    var minRequiredOverlap = Math.Max(minOverlapMeters, Math.Min(sourceLen, correctionLen) * 0.25);
                    if (overlap < minRequiredOverlap)
                    {
                        continue;
                    }
                }

                var signedSourceToCorrection = DistancePointToInfiniteLine(sourceMid, correction.A, correction.B);
                var sourceToCorrection = Math.Abs(signedSourceToCorrection);
                var sourceInsetError = Math.Abs(sourceToCorrection - expectedInsetMeters);
                if (sourceInsetError > insetToleranceMeters)
                {
                    continue;
                }

                var correctionMid = Midpoint(correction.A, correction.B);
                var correctionToSource = Math.Abs(DistancePointToInfiniteLine(correctionMid, sourceA, sourceB));
                var correctionInsetError = Math.Abs(correctionToSource - expectedInsetMeters);
                if (correctionInsetError > insetToleranceMeters)
                {
                    continue;
                }

                var combinedInsetError = sourceInsetError + correctionInsetError;
                if (!found || combinedInsetError < bestInsetError)
                {
                    bestInsetError = combinedInsetError;
                    bestMatch = new GhostMatchInfo(signedSourceToCorrection);
                    found = true;
                }
            }

            if (!found)
            {
                return false;
            }

            matchInfo = bestMatch;
            return true;
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
            if (lenA <= 1e-6)
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
            if (len <= 1e-6)
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
    }
}
