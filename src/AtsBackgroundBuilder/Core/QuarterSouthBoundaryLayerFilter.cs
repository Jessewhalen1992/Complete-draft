using System;

namespace AtsBackgroundBuilder
{
    internal static class QuarterSouthBoundaryLayerFilter
    {
        public static bool IsOrdinaryResolutionLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            return string.Equals(layerName, "L-USEC-0", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
        }
    }
}
