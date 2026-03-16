namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionOuterBridgePropagationPolicy
    {
        public static bool ShouldRelayerBridgeSegment(bool startTouchesCorrection, bool endTouchesCorrection)
        {
            return startTouchesCorrection && endTouchesCorrection;
        }
    }
}
