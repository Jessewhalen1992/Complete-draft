using System;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionOuterFallbackLayerClassifier
    {
        private const string UsecBaseLayer = "L-USEC";
        private const string UsecZeroLayer = "L-USEC-0";
        private const string UsecTwentyLayer = "L-USEC2012";
        private const string UsecTwentyDashedLayer = "L-USEC-2012";
        private const string UsecThirtyLayer = "L-USEC3018";
        private const string UsecThirtyDashedLayer = "L-USEC-3018";

        public static bool IsCorrectionOuterFallbackLayer(string layerName, bool preferSouthSide)
        {
            if (IsUsecTwentyLike(layerName) ||
                string.Equals(layerName, UsecBaseLayer, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return preferSouthSide &&
                   string.Equals(layerName, UsecZeroLayer, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsCorrectionOuterBridgeLayer(string layerName)
        {
            return IsUsecTwentyLike(layerName) ||
                   string.Equals(layerName, UsecBaseLayer, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, UsecZeroLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUsecTwentyLike(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            return string.Equals(layerName, UsecTwentyLayer, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, UsecTwentyDashedLayer, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, UsecThirtyLayer, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, UsecThirtyDashedLayer, StringComparison.OrdinalIgnoreCase);
        }
    }
}
