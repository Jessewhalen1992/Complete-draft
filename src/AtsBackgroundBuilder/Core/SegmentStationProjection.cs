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
        private const double StationDeltaTolerance = 1e-9;

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
            if (!BoundaryStationSpanPolicy.IsWithinSegmentSpan(station, aStation, bStation, stationTolerance))
            {
                return false;
            }

            var stationDelta = bStation - aStation;
            if (Math.Abs(stationDelta) <= StationDeltaTolerance)
            {
                return false;
            }

            var t = Clamp01((station - aStation) / stationDelta);
            point = Interpolate(segmentStart, segmentEnd, t);
            return true;
        }

        private static double Project(ProjectedStationPoint point, ProjectedStationVector axisUnit)
        {
            return (point.X * axisUnit.X) + (point.Y * axisUnit.Y);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }

            if (value > 1.0)
            {
                return 1.0;
            }

            return value;
        }

        private static ProjectedStationPoint Interpolate(
            ProjectedStationPoint start,
            ProjectedStationPoint end,
            double t)
        {
            return new ProjectedStationPoint(
                start.X + ((end.X - start.X) * t),
                start.Y + ((end.Y - start.Y) * t));
        }
    }
}
