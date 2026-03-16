namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionBoundaryTrendSampling
    {
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

            var dx = rightAnchor.X - leftAnchor.X;
            var dy = rightAnchor.Y - leftAnchor.Y;
            if (System.Math.Abs(dx) <= 1e-6)
            {
                return false;
            }

            var slope = dy / dx;
            var intercept = boundaryAnchor.Y - (slope * boundaryAnchor.X);
            sampleA = new LineDistancePoint(minX, (slope * minX) + intercept);
            sampleB = new LineDistancePoint(maxX, (slope * maxX) + intercept);
            return true;
        }
    }
}
