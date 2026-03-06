using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal readonly struct PlsrQuarterMatchPoint
    {
        public PlsrQuarterMatchPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal readonly struct PlsrQuarterMatchBounds
    {
        public PlsrQuarterMatchBounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }

        public bool Contains(PlsrQuarterMatchPoint point)
        {
            return point.X >= MinX &&
                   point.X <= MaxX &&
                   point.Y >= MinY &&
                   point.Y <= MaxY;
        }
    }

    internal readonly struct PlsrQuarterTouchCandidate
    {
        public PlsrQuarterTouchCandidate(string key, int score, double overlapArea)
        {
            Key = key ?? string.Empty;
            Score = score;
            OverlapArea = overlapArea;
        }

        public string Key { get; }
        public int Score { get; }
        public double OverlapArea { get; }
    }

    internal sealed class PlsrQuarterTouchResolution
    {
        public static readonly PlsrQuarterTouchResolution Empty = new PlsrQuarterTouchResolution(
            string.Empty,
            Array.Empty<string>());

        public PlsrQuarterTouchResolution(string primaryQuarterKey, IReadOnlyList<string> touchedQuarterKeys)
        {
            PrimaryQuarterKey = primaryQuarterKey ?? string.Empty;
            TouchedQuarterKeys = touchedQuarterKeys ?? Array.Empty<string>();
        }

        public string PrimaryQuarterKey { get; }
        public IReadOnlyList<string> TouchedQuarterKeys { get; }
    }

    internal static class PlsrQuarterPointBuilder
    {
        public static List<PlsrQuarterMatchPoint> BuildDimensionPoints(
            PlsrQuarterMatchPoint anchorPoint,
            PlsrQuarterMatchPoint? textPoint,
            PlsrQuarterMatchPoint? firstExtensionPoint,
            PlsrQuarterMatchPoint? secondExtensionPoint)
        {
            var points = new List<PlsrQuarterMatchPoint>();
            AddUnique(points, anchorPoint);

            if (textPoint.HasValue)
            {
                AddUnique(points, textPoint.Value);
            }

            if (firstExtensionPoint.HasValue)
            {
                AddUnique(points, firstExtensionPoint.Value);
            }

            if (secondExtensionPoint.HasValue)
            {
                AddUnique(points, secondExtensionPoint.Value);
            }

            if (firstExtensionPoint.HasValue && secondExtensionPoint.HasValue)
            {
                var first = firstExtensionPoint.Value;
                var second = secondExtensionPoint.Value;
                AddUnique(points, new PlsrQuarterMatchPoint(
                    (first.X + second.X) * 0.5,
                    (first.Y + second.Y) * 0.5));
            }

            return points;
        }

        public static List<PlsrQuarterMatchPoint> BuildExtentPoints(
            PlsrQuarterMatchPoint anchorPoint,
            PlsrQuarterMatchPoint? minPoint,
            PlsrQuarterMatchPoint? maxPoint)
        {
            var points = new List<PlsrQuarterMatchPoint>();
            AddUnique(points, anchorPoint);

            if (!minPoint.HasValue || !maxPoint.HasValue)
            {
                return points;
            }

            var min = minPoint.Value;
            var max = maxPoint.Value;
            var centerX = (min.X + max.X) * 0.5;
            var centerY = (min.Y + max.Y) * 0.5;

            AddUnique(points, min);
            AddUnique(points, max);
            AddUnique(points, new PlsrQuarterMatchPoint(max.X, min.Y));
            AddUnique(points, new PlsrQuarterMatchPoint(min.X, max.Y));
            AddUnique(points, new PlsrQuarterMatchPoint(centerX, centerY));
            AddUnique(points, new PlsrQuarterMatchPoint(centerX, min.Y));
            AddUnique(points, new PlsrQuarterMatchPoint(centerX, max.Y));
            AddUnique(points, new PlsrQuarterMatchPoint(min.X, centerY));
            AddUnique(points, new PlsrQuarterMatchPoint(max.X, centerY));

            return points;
        }

        private static void AddUnique(List<PlsrQuarterMatchPoint> points, PlsrQuarterMatchPoint candidate)
        {
            const double epsilon = 1e-6;

            foreach (var existing in points)
            {
                if (Math.Abs(existing.X - candidate.X) <= epsilon &&
                    Math.Abs(existing.Y - candidate.Y) <= epsilon)
                {
                    return;
                }
            }

            points.Add(candidate);
        }
    }

    internal static class PlsrQuarterPointMatcher
    {
        public static bool MatchesAnyPoint(
            PlsrQuarterMatchBounds bounds,
            IEnumerable<PlsrQuarterMatchPoint>? candidatePoints,
            Func<PlsrQuarterMatchPoint, bool> isInsideQuarter)
        {
            if (isInsideQuarter == null)
            {
                throw new ArgumentNullException(nameof(isInsideQuarter));
            }

            if (candidatePoints == null)
            {
                return false;
            }

            foreach (var candidatePoint in candidatePoints)
            {
                if (!bounds.Contains(candidatePoint))
                {
                    continue;
                }

                if (isInsideQuarter(candidatePoint))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static class PlsrQuarterTouchResolver
    {
        public static PlsrQuarterTouchResolution Resolve(IEnumerable<PlsrQuarterTouchCandidate>? candidates)
        {
            if (candidates == null)
            {
                return PlsrQuarterTouchResolution.Empty;
            }

            var touchedQuarterKeys = new List<string>();
            var seenQuarterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryQuarterKey = string.Empty;
            var bestScore = int.MinValue;
            var bestOverlapArea = double.NegativeInfinity;

            foreach (var candidate in candidates)
            {
                if (candidate.Score <= 0 || string.IsNullOrWhiteSpace(candidate.Key))
                {
                    continue;
                }

                if (seenQuarterKeys.Add(candidate.Key))
                {
                    touchedQuarterKeys.Add(candidate.Key);
                }

                if (candidate.Score > bestScore ||
                    (candidate.Score == bestScore && candidate.OverlapArea > bestOverlapArea))
                {
                    bestScore = candidate.Score;
                    bestOverlapArea = candidate.OverlapArea;
                    primaryQuarterKey = candidate.Key;
                }
            }

            if (touchedQuarterKeys.Count == 0)
            {
                return PlsrQuarterTouchResolution.Empty;
            }

            return new PlsrQuarterTouchResolution(primaryQuarterKey, touchedQuarterKeys);
        }
    }
}
