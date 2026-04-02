using System;

namespace AtsBackgroundBuilder
{
    internal static class BoundaryStationSpanPolicy
    {
        public static bool IsWithinSegmentSpan(double station, double stationA, double stationB, double pad)
        {
            var bounds = BuildInclusiveBounds(stationA, stationB, pad);
            return station >= bounds.Min && station <= bounds.Max;
        }

        private static InclusiveBounds BuildInclusiveBounds(double valueA, double valueB, double pad)
        {
            return new InclusiveBounds(
                Math.Min(valueA, valueB) - pad,
                Math.Max(valueA, valueB) + pad);
        }

        private readonly struct InclusiveBounds
        {
            public InclusiveBounds(double min, double max)
            {
                Min = min;
                Max = max;
            }

            public double Min { get; }

            public double Max { get; }
        }
    }
}
