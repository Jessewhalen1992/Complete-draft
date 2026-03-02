using System;

namespace AtsBackgroundBuilder.Core
{
    internal enum UiNoResultAction
    {
        ReopenWithSnapshot,
        ReopenWithoutSnapshot,
        RecoverSnapshot,
        Cancel,
    }

    internal sealed class UiNoResultDecision
    {
        public UiNoResultDecision(
            UiNoResultAction action,
            int nextAutoCloseResumeAttempts,
            bool shouldLogRecoveryExhausted,
            bool shouldLogSnapshotUnavailable,
            string snapshotUnavailablePrefix,
            bool shouldLogClosedWithoutBuildAttempt,
            bool shouldLogFallbackUnavailable)
        {
            Action = action;
            NextAutoCloseResumeAttempts = nextAutoCloseResumeAttempts;
            ShouldLogRecoveryExhausted = shouldLogRecoveryExhausted;
            ShouldLogSnapshotUnavailable = shouldLogSnapshotUnavailable;
            SnapshotUnavailablePrefix = snapshotUnavailablePrefix ?? string.Empty;
            ShouldLogClosedWithoutBuildAttempt = shouldLogClosedWithoutBuildAttempt;
            ShouldLogFallbackUnavailable = shouldLogFallbackUnavailable;
        }

        public UiNoResultAction Action { get; }
        public int NextAutoCloseResumeAttempts { get; }
        public bool ShouldLogRecoveryExhausted { get; }
        public bool ShouldLogSnapshotUnavailable { get; }
        public string SnapshotUnavailablePrefix { get; }
        public bool ShouldLogClosedWithoutBuildAttempt { get; }
        public bool ShouldLogFallbackUnavailable { get; }
    }

    internal static class UiSessionRecoveryDecisionEngine
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
            var isNoIntentClose = !explicitCancel && !buildRequested && !buildAttempted;
            if (isNoIntentClose)
            {
                if (snapshotAvailable && autoCloseResumeAttempts < maxAutoCloseResumeAttempts)
                {
                    return new UiNoResultDecision(
                        UiNoResultAction.ReopenWithSnapshot,
                        autoCloseResumeAttempts + 1,
                        shouldLogRecoveryExhausted: false,
                        shouldLogSnapshotUnavailable: false,
                        snapshotUnavailablePrefix: string.Empty,
                        shouldLogClosedWithoutBuildAttempt: false,
                        shouldLogFallbackUnavailable: false);
                }

                if (boundaryRoundTripUsed && autoCloseResumeAttempts < maxAutoCloseResumeAttempts)
                {
                    return new UiNoResultDecision(
                        UiNoResultAction.ReopenWithoutSnapshot,
                        autoCloseResumeAttempts + 1,
                        shouldLogRecoveryExhausted: false,
                        shouldLogSnapshotUnavailable: false,
                        snapshotUnavailablePrefix: string.Empty,
                        shouldLogClosedWithoutBuildAttempt: false,
                        shouldLogFallbackUnavailable: false);
                }
            }

            var normalizedBuildTrace = buildTrace ?? string.Empty;
            var buildAbortedByValidation = normalizedBuildTrace.StartsWith("onbuild_abort_", StringComparison.OrdinalIgnoreCase);
            var shouldTrySnapshot = !explicitCancel && (buildRequested || (buildAttempted && !buildAbortedByValidation));
            if (shouldTrySnapshot && snapshotAvailable)
            {
                return new UiNoResultDecision(
                    UiNoResultAction.RecoverSnapshot,
                    nextAutoCloseResumeAttempts: 0,
                    shouldLogRecoveryExhausted: false,
                    shouldLogSnapshotUnavailable: false,
                    snapshotUnavailablePrefix: string.Empty,
                    shouldLogClosedWithoutBuildAttempt: false,
                    shouldLogFallbackUnavailable: false);
            }

            return new UiNoResultDecision(
                UiNoResultAction.Cancel,
                autoCloseResumeAttempts,
                shouldLogRecoveryExhausted: isNoIntentClose && snapshotAvailable,
                shouldLogSnapshotUnavailable: isNoIntentClose && !snapshotAvailable,
                snapshotUnavailablePrefix: boundaryRoundTripUsed
                    ? "UI boundary-import round-trip snapshot unavailable"
                    : "UI auto-close snapshot unavailable",
                shouldLogClosedWithoutBuildAttempt: !explicitCancel && !buildRequested && !buildAttempted,
                shouldLogFallbackUnavailable: !explicitCancel && (buildRequested || buildAttempted));
        }
    }
}
