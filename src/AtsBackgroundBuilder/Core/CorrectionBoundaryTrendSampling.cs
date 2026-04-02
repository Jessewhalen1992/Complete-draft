namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionBoundaryTrendSampling
    {
        private const double HorizontalTolerance = 1e-6;

        public static bool TryBuildBoundarySampleAcrossXSpan(
            LineDistancePoint boundaryAnchor,
            LineDistancePoint leftAnchor,
            LineDistancePoint rightAnchor,
            double minX,
            double maxX,
            out LineDistancePoint sampleA,
            out LineDistancePoint sampleB)
        {
            sampleA = boundaryAnchor;
            sampleB = boundaryAnchor;
            if (maxX <= minX)
            {
                return false;
            }

            if (!TryBuildTrendLine(boundaryAnchor, leftAnchor, rightAnchor, out var slope, out var intercept))
            {
                return false;
            }

            sampleA = CreateTrendSample(minX, slope, intercept);
            sampleB = CreateTrendSample(maxX, slope, intercept);
            return true;
        }

        private static bool TryBuildTrendLine(
            LineDistancePoint boundaryAnchor,
            LineDistancePoint leftAnchor,
            LineDistancePoint rightAnchor,
            out double slope,
            out double intercept)
        {
            slope = 0.0;
            intercept = 0.0;

            var dx = rightAnchor.X - leftAnchor.X;
            if (System.Math.Abs(dx) <= HorizontalTolerance)
            {
                return false;
            }

            var dy = rightAnchor.Y - leftAnchor.Y;
            slope = dy / dx;
            intercept = boundaryAnchor.Y - (slope * boundaryAnchor.X);
            return true;
        }

        private static LineDistancePoint CreateTrendSample(double x, double slope, double intercept)
        {
            return new LineDistancePoint(x, (slope * x) + intercept);
        }
    }
}
