using System;

namespace AtsBackgroundBuilder
{
    internal readonly struct LineDistancePoint
    {
        public LineDistancePoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal static class PerpendicularLineDistanceMeasurement
    {
        public static double SignedDistanceToLine(LineDistancePoint point, double slope, double intercept)
        {
            var numerator = point.Y - ((slope * point.X) + intercept);
            var denominator = Math.Sqrt(1.0 + (slope * slope));
            return numerator / denominator;
        }
    }

    internal static class PerpendicularLineBandMeasurement
    {
        public static (double Min, double Max) SignedDistanceRangeToLine(
            LineDistancePoint a,
            LineDistancePoint b,
            double slope,
            double intercept)
        {
            var offsetA = PerpendicularLineDistanceMeasurement.SignedDistanceToLine(a, slope, intercept);
            var offsetB = PerpendicularLineDistanceMeasurement.SignedDistanceToLine(b, slope, intercept);
            return offsetA <= offsetB
                ? (offsetA, offsetB)
                : (offsetB, offsetA);
        }

        public static bool IntersectsStrip(
            LineDistancePoint a,
            LineDistancePoint b,
            double southSlope,
            double southIntercept,
            double northSlope,
            double northIntercept,
            double tolerance)
        {
            var southRange = SignedDistanceRangeToLine(a, b, southSlope, southIntercept);
            if (southRange.Max < -tolerance)
            {
                return false;
            }

            var northRange = SignedDistanceRangeToLine(a, b, northSlope, northIntercept);
            if (northRange.Min > tolerance)
            {
                return false;
            }

            return true;
        }
    }
}
