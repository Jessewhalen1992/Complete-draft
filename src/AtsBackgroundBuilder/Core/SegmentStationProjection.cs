using System;

namespace AtsBackgroundBuilder
{
    internal readonly struct ProjectedStationPoint
    {
        public ProjectedStationPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal readonly struct ProjectedStationVector
    {
        public ProjectedStationVector(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal static class SegmentStationProjection
    {
        public static bool TryResolvePointAtStation(
            ProjectedStationPoint segmentStart,
            ProjectedStationPoint segmentEnd,
            ProjectedStationVector axisUnit,
            double station,
            double stationTolerance,
            out ProjectedStationPoint point)
        {
            point = default;

            var aStation = Project(segmentStart, axisUnit);
            var bStation = Project(segmentEnd, axisUnit);
            var minStation = Math.Min(aStation, bStation) - stationTolerance;
            var maxStation = Math.Max(aStation, bStation) + stationTolerance;
            if (station < minStation || station > maxStation)
            {
                return false;
            }

            var stationDelta = bStation - aStation;
            if (Math.Abs(stationDelta) <= 1e-9)
            {
                return false;
            }

            var t = (station - aStation) / stationDelta;
            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            point = new ProjectedStationPoint(
                segmentStart.X + ((segmentEnd.X - segmentStart.X) * t),
                segmentStart.Y + ((segmentEnd.Y - segmentStart.Y) * t));
            return true;
        }

        private static double Project(ProjectedStationPoint point, ProjectedStationVector axisUnit)
        {
            return (point.X * axisUnit.X) + (point.Y * axisUnit.Y);
        }
    }
}
