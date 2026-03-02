using System;

namespace AtsBackgroundBuilder.Core
{
    internal static class UiSessionRecoveryService
    {
        public static UiNoResultDecision EvaluateNoResult(
            bool explicitCancel,
            bool buildRequested,
            bool buildAttempted,
            bool boundaryRoundTripUsed,
            bool snapshotAvailable,
            string buildTrace,
            int autoCloseResumeAttempts,
            int maxAutoCloseResumeAttempts)
        {
            return UiSessionRecoveryDecisionEngine.EvaluateNoResult(
                explicitCancel,
                buildRequested,
                buildAttempted,
                boundaryRoundTripUsed,
                snapshotAvailable,
                buildTrace,
                autoCloseResumeAttempts,
                maxAutoCloseResumeAttempts);
        }

        public static bool TryRestorePersistedPlsrSelectionFromAutoCloseFallback(
            bool dialogAccepted,
            AtsBuildInput? input,
            out string logMessage)
        {
            logMessage = string.Empty;
            if (dialogAccepted || input == null || input.CheckPlsr || input.IncludeSurfaceImpact)
            {
                return false;
            }

            if (!AtsBuildWindow.TryGetPersistedPlsrXmlPaths(out var persistedPlsrXmlPaths))
            {
                return false;
            }

            if (AtsBuildWindow.TryGetPersistedPlsrOptionSelection(
                    out var persistedCheckPlsr,
                    out var persistedSurfaceImpact))
            {
                input.CheckPlsr = persistedCheckPlsr;
                input.IncludeSurfaceImpact = persistedSurfaceImpact;
            }

            if (!input.CheckPlsr && !input.IncludeSurfaceImpact)
            {
                input.CheckPlsr = true;
            }

            input.PlsrXmlPaths.Clear();
            input.PlsrXmlPaths.AddRange(persistedPlsrXmlPaths);
            logMessage =
                $"UI auto-close fallback: restored persisted options CheckPLSR={(input.CheckPlsr ? "ON" : "OFF")}, SurfaceImpact={(input.IncludeSurfaceImpact ? "ON" : "OFF")} using {persistedPlsrXmlPaths.Count} persisted XML file(s).";
            return true;
        }
    }
}
