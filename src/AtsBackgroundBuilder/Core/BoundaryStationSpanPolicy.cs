using System;

namespace AtsBackgroundBuilder
{
    internal static class BoundaryStationSpanPolicy
    {
        public static bool IsWithinSegmentSpan(double station, double stationA, double stationB, double pad)
        {
            var minStation = Math.Min(stationA, stationB) - pad;
            var maxStation = Math.Max(stationA, stationB) + pad;
            return station >= minStation && station <= maxStation;
        }
    }
}
