using System;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    internal readonly struct RangeEdgeAnchor
    {
        public RangeEdgeAnchor(bool horizontal, double axis, double spanMin, double spanMax)
        {
            Horizontal = horizontal;
            Axis = axis;
            SpanMin = spanMin;
            SpanMax = spanMax;
        }

        public bool Horizontal { get; }

        public double Axis { get; }

        public double SpanMin { get; }

        public double SpanMax { get; }

        public static RangeEdgeAnchor FromSegment(Point2d a, Point2d b, bool horizontal)
        {
            return new RangeEdgeAnchor(
                horizontal,
                horizontal ? 0.5 * (a.Y + b.Y) : 0.5 * (a.X + b.X),
                horizontal ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y),
                horizontal ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y));
        }

        public bool Matches(RangeEdgeAnchor other, double axisTolerance, double minSpanOverlap, double maxSpanGap)
        {
            if (Horizontal != other.Horizontal)
            {
                return false;
            }

            if (Math.Abs(Axis - other.Axis) > axisTolerance)
            {
                return false;
            }

            return HasSpanOverlap(other, minSpanOverlap) || GetSpanGap(other) <= maxSpanGap;
        }

        private bool HasSpanOverlap(RangeEdgeAnchor other, double minOverlap)
        {
            var overlap = Math.Min(SpanMax, other.SpanMax) - Math.Max(SpanMin, other.SpanMin);
            return overlap >= minOverlap;
        }

        private double GetSpanGap(RangeEdgeAnchor other)
        {
            if (SpanMax < other.SpanMin)
            {
                return other.SpanMin - SpanMax;
            }

            if (other.SpanMax < SpanMin)
            {
                return SpanMin - other.SpanMax;
            }

            return 0.0;
        }
    }
}
