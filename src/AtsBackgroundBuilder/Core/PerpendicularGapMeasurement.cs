using System;

namespace AtsBackgroundBuilder.Core
{
    internal readonly struct GapPoint
    {
        public GapPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal static class PerpendicularGapMeasurement
    {
        private static readonly double[] SampleFractions = { 0.25, 0.50, 0.75 };

        public static bool TryMeasureBetweenFacingEdges(
            GapPoint baseStart,
            GapPoint baseEnd,
            GapPoint otherStart,
            GapPoint otherEnd,
            out double gapMeters)
        {
            gapMeters = 0.0;

            var baseDx = baseEnd.X - baseStart.X;
            var baseDy = baseEnd.Y - baseStart.Y;
            var otherDx = otherEnd.X - otherStart.X;
            var otherDy = otherEnd.Y - otherStart.Y;

            var baseLength = Math.Sqrt((baseDx * baseDx) + (baseDy * baseDy));
            var otherLength = Math.Sqrt((otherDx * otherDx) + (otherDy * otherDy));
            if (baseLength <= 1e-6 || otherLength <= 1e-6)
            {
                return false;
            }

            var baseUx = baseDx / baseLength;
            var baseUy = baseDy / baseLength;
            var otherUx = otherDx / otherLength;
            var otherUy = otherDy / otherLength;

            var parallelDot = (baseUx * otherUx) + (baseUy * otherUy);
            if (Math.Abs(parallelDot) < 0.99)
            {
                return false;
            }

            if (parallelDot < 0.0)
            {
                otherDx = -otherDx;
                otherDy = -otherDy;
                var tmp = otherStart;
                otherStart = otherEnd;
                otherEnd = tmp;
            }

            var tOther0 = Dot(otherStart.X - baseStart.X, otherStart.Y - baseStart.Y, baseUx, baseUy);
            var tOther1 = Dot(otherEnd.X - baseStart.X, otherEnd.Y - baseStart.Y, baseUx, baseUy);
            var overlapMin = Math.Max(0.0, Math.Min(tOther0, tOther1));
            var overlapMax = Math.Min(baseLength, Math.Max(tOther0, tOther1));

            const double endpointSnapToleranceMeters = 1.0;
            if (overlapMin > 0.0 && overlapMin < endpointSnapToleranceMeters)
            {
                overlapMin = 0.0;
            }

            if (overlapMax < baseLength && (baseLength - overlapMax) < endpointSnapToleranceMeters)
            {
                overlapMax = baseLength;
            }

            var overlapLength = overlapMax - overlapMin;
            var minRequiredOverlap = Math.Max(100.0, Math.Min(baseLength, otherLength) * 0.75);
            if (overlapLength < minRequiredOverlap)
            {
                return false;
            }

            var otherLengthSquared = (otherDx * otherDx) + (otherDy * otherDy);
            if (otherLengthSquared <= 1e-9)
            {
                return false;
            }

            var gaps = new double[SampleFractions.Length];
            for (var i = 0; i < SampleFractions.Length; i++)
            {
                var sampleDistance = overlapMin + (overlapLength * SampleFractions[i]);
                var baseSampleX = baseStart.X + (baseUx * sampleDistance);
                var baseSampleY = baseStart.Y + (baseUy * sampleDistance);
                var projection = Dot(
                    baseSampleX - otherStart.X,
                    baseSampleY - otherStart.Y,
                    otherDx,
                    otherDy) / otherLengthSquared;
                projection = Math.Max(0.0, Math.Min(1.0, projection));

                var otherSampleX = otherStart.X + (otherDx * projection);
                var otherSampleY = otherStart.Y + (otherDy * projection);
                var normalX = -baseUy;
                var normalY = baseUx;
                if (Dot(otherSampleX - baseSampleX, otherSampleY - baseSampleY, normalX, normalY) < 0.0)
                {
                    normalX = -normalX;
                    normalY = -normalY;
                }

                gaps[i] = Math.Abs(Dot(otherSampleX - baseSampleX, otherSampleY - baseSampleY, normalX, normalY));
            }

            Array.Sort(gaps);
            gapMeters = gaps[gaps.Length / 2];
            return gapMeters >= 0.0;
        }

        private static double Dot(double ax, double ay, double bx, double by)
        {
            return (ax * bx) + (ay * by);
        }
    }
}
