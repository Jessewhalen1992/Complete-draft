using System;

namespace AtsBackgroundBuilder.Core
{
    internal static class DispositionLabelColorPolicy
    {
        internal const int ReviewGreenColorIndex = 3;
        private const string VariableWidthToken = "Variable Width";

        public static int ResolveTextColorIndex(string? labelText, int requestedColorIndex)
        {
            if (!string.IsNullOrWhiteSpace(labelText) &&
                labelText.IndexOf(VariableWidthToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ReviewGreenColorIndex;
            }

            return requestedColorIndex;
        }
    }
}