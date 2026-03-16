using System;

namespace AtsBackgroundBuilder
{
    internal static class OrdinaryUsecTieInAnchorLayerClassifier
    {
        public static bool IsOuterUsecAnchorLayer(string? layer)
        {
            return string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
        }
    }
}
