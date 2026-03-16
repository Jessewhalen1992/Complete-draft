using System;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionBandAxisClassifier
    {
        public static bool IsCloserToOuterBand(double centerDistanceFromSeam, double fullRoadAllowanceWidth, double insetFromOuterBand)
        {
            var outerBandDistance = Math.Max(0.0, fullRoadAllowanceWidth * 0.5);
            var innerBandDistance = Math.Max(0.0, outerBandDistance - Math.Max(0.0, insetFromOuterBand));
            var outerDelta = Math.Abs(centerDistanceFromSeam - outerBandDistance);
            var innerDelta = Math.Abs(centerDistanceFromSeam - innerBandDistance);
            return outerDelta + 1e-6 < innerDelta;
        }

        public static bool IsCloserToInnerBand(double centerDistanceFromSeam, double fullRoadAllowanceWidth, double insetFromOuterBand)
        {
            var outerBandDistance = Math.Max(0.0, fullRoadAllowanceWidth * 0.5);
            var innerBandDistance = Math.Max(0.0, outerBandDistance - Math.Max(0.0, insetFromOuterBand));
            var outerDelta = Math.Abs(centerDistanceFromSeam - outerBandDistance);
            var innerDelta = Math.Abs(centerDistanceFromSeam - innerBandDistance);
            return innerDelta + 1e-6 < outerDelta;
        }
    }
}
