using System;
using System.Collections.Generic;
using AtsBackgroundBuilder;
using AtsBackgroundBuilder.Core;

internal static class Program
{
    private const string BoundaryUnavailablePrefix = "UI boundary-import round-trip snapshot unavailable";
    private const string AutoCloseUnavailablePrefix = "UI auto-close snapshot unavailable";

    private static int Main()
    {
        try
        {
            RunAll();
            Console.WriteLine("Decision tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Decision tests failed: " + ex.Message);
            return 1;
        }
    }

    private static void RunAll()
    {
        TestPromptLifecycleRefreshRunsOnSuccess();
        TestPromptLifecycleRefreshRunsOnException();
        TestReviewDecisionResolveAcceptedIdsHonorsApplyFlag();
        TestReviewDecisionResolveAcceptedIdsIncludesOnlyAccept();
        TestSectionRequestParserCarryDownAndQuarterDefault();
        TestSectionRequestParserExpandAllSections();
        TestSectionRequestParserMissingBaseFieldsFails();
        TestSectionRequestParserMissingSectionFails();
        TestSectionRequestParserInvalidQuarterFails();

        TestNoIntentSnapshotReopen();
        TestNoIntentBoundaryRoundTripReopenWithoutSnapshot();
        TestNoIntentRecoveryExhaustedCancels();
        TestNoIntentSnapshotUnavailableLogsBoundaryPrefix();
        TestBuildRequestedSnapshotRecovers();
        TestBuildAttemptAbortedByValidationCancels();
        TestBuildAttemptNonValidationAbortRecovers();
        TestExplicitCancelAlwaysCancels();
        TestBuildRequestedWithoutSnapshotCancels();
        TestNoIntentSnapshotUnavailableUsesAutoClosePrefix();

        TestQuarterVisibilityPolicyMatrix();
        TestCleanupPlanMatrix();
        TestBuildExecutionPlanDefaults();
        TestBuildExecutionPlanQuarterVisibility();
        TestBuildExecutionPlanPlsrDrivesScopeAndImport();
        TestBuildExecutionPlanLabelPlacementOrdering();
        TestBuildExecutionPlanPlsrMissingLabelPrecheckGate();
        TestBuildExecutionPlanSupplementalSectionInfoGate();
        TestBuildExecutionPlanPassThroughFlags();

        TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored();
        TestPlsrApplyDecisionEnginePreservesAcceptedOrder();
        TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted();

        TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes();
        TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred();
        TestPlsrMissingLabelCandidateSelectorSkipsBlankCandidates();

        TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes();
        TestPlsrSummaryComposerBuildsWarningWithSortedExamples();
        TestPlsrSummaryComposerSkipsWarningWhenTextFallbackAllowed();
    }

    private static void TestPromptLifecycleRefreshRunsOnSuccess()
    {
        var refreshCount = 0;
        var result = PromptLifecycleService.ExecuteWithPromptRefresh(
            () => 42,
            () => refreshCount++);

        AssertEqual(42, result, nameof(TestPromptLifecycleRefreshRunsOnSuccess));
        AssertEqual(1, refreshCount, nameof(TestPromptLifecycleRefreshRunsOnSuccess));
    }

    private static void TestPromptLifecycleRefreshRunsOnException()
    {
        var refreshCount = 0;
        var threw = false;
        try
        {
            _ = PromptLifecycleService.ExecuteWithPromptRefresh<int>(
                () => throw new InvalidOperationException("boom"),
                () => refreshCount++);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        AssertEqual(true, threw, nameof(TestPromptLifecycleRefreshRunsOnException));
        AssertEqual(1, refreshCount, nameof(TestPromptLifecycleRefreshRunsOnException));
    }

    private static void TestReviewDecisionResolveAcceptedIdsHonorsApplyFlag()
    {
        var issueId = Guid.NewGuid();
        var resolved = ReviewDecisionService.ResolveAcceptedIssueIds(
            applyRequested: false,
            new[] { new ReviewDecisionEntry(issueId, "Accept") });

        AssertEqual(0, resolved.Count, nameof(TestReviewDecisionResolveAcceptedIdsHonorsApplyFlag));
    }

    private static void TestReviewDecisionResolveAcceptedIdsIncludesOnlyAccept()
    {
        var acceptA = Guid.NewGuid();
        var acceptB = Guid.NewGuid();
        var resolved = ReviewDecisionService.ResolveAcceptedIssueIds(
            applyRequested: true,
            new[]
            {
                new ReviewDecisionEntry(acceptA, "Accept"),
                new ReviewDecisionEntry(acceptA, "accept"),
                new ReviewDecisionEntry(acceptB, "ACCEPT"),
                new ReviewDecisionEntry(Guid.NewGuid(), "Ignore")
            });

        AssertEqual(true, resolved.Contains(acceptA), nameof(TestReviewDecisionResolveAcceptedIdsIncludesOnlyAccept));
        AssertEqual(true, resolved.Contains(acceptB), nameof(TestReviewDecisionResolveAcceptedIdsIncludesOnlyAccept));
        AssertEqual(2, resolved.Count, nameof(TestReviewDecisionResolveAcceptedIdsIncludesOnlyAccept));
    }

    private static void TestSectionRequestParserCarryDownAndQuarterDefault()
    {
        var result = SectionRequestParser.Parse(
            zone: 11,
            new[]
            {
                new SectionRequestRowInput("4", "5", "52", "1", ""),
                new SectionRequestRowInput("", "", "", "2", "NE")
            });

        AssertEqual(true, result.IsSuccess, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
        AssertEqual(2, result.Requests.Count, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
        AssertEqual(QuarterSelection.All, result.Requests[0].Quarter, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
        AssertEqual("1", result.Requests[0].Key.Section, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
        AssertEqual(QuarterSelection.NorthEast, result.Requests[1].Quarter, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
        AssertEqual("2", result.Requests[1].Key.Section, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
        AssertEqual("52", result.Requests[1].Key.Township, nameof(TestSectionRequestParserCarryDownAndQuarterDefault));
    }

    private static void TestSectionRequestParserExpandAllSections()
    {
        var result = SectionRequestParser.Parse(
            zone: 12,
            new[]
            {
                new SectionRequestRowInput("4", "5", "52", "", "SW")
            });

        AssertEqual(true, result.IsSuccess, nameof(TestSectionRequestParserExpandAllSections));
        AssertEqual(36, result.Requests.Count, nameof(TestSectionRequestParserExpandAllSections));
        AssertEqual("1", result.Requests[0].Key.Section, nameof(TestSectionRequestParserExpandAllSections));
        AssertEqual("36", result.Requests[^1].Key.Section, nameof(TestSectionRequestParserExpandAllSections));
        AssertEqual(QuarterSelection.SouthWest, result.Requests[0].Quarter, nameof(TestSectionRequestParserExpandAllSections));
        AssertEqual(12, result.Requests[0].Key.Zone, nameof(TestSectionRequestParserExpandAllSections));
    }

    private static void TestSectionRequestParserMissingBaseFieldsFails()
    {
        var result = SectionRequestParser.Parse(
            zone: 11,
            new[]
            {
                new SectionRequestRowInput("", "", "", "1", "NW")
            });

        AssertEqual(false, result.IsSuccess, nameof(TestSectionRequestParserMissingBaseFieldsFails));
        AssertEqual(SectionRequestParseFailure.MissingMeridianRangeTownship, result.Failure, nameof(TestSectionRequestParserMissingBaseFieldsFails));
        AssertEqual(0, result.Requests.Count, nameof(TestSectionRequestParserMissingBaseFieldsFails));
    }

    private static void TestSectionRequestParserMissingSectionFails()
    {
        var result = SectionRequestParser.Parse(
            zone: 11,
            new[]
            {
                new SectionRequestRowInput("4", "5", "52", "", "ALL"),
                new SectionRequestRowInput("", "", "", "", "NE")
            });

        AssertEqual(false, result.IsSuccess, nameof(TestSectionRequestParserMissingSectionFails));
        AssertEqual(SectionRequestParseFailure.MissingSection, result.Failure, nameof(TestSectionRequestParserMissingSectionFails));
        AssertEqual(0, result.Requests.Count, nameof(TestSectionRequestParserMissingSectionFails));
    }

    private static void TestSectionRequestParserInvalidQuarterFails()
    {
        var result = SectionRequestParser.Parse(
            zone: 11,
            new[]
            {
                new SectionRequestRowInput("4", "5", "52", "1", "BAD")
            });

        AssertEqual(false, result.IsSuccess, nameof(TestSectionRequestParserInvalidQuarterFails));
        AssertEqual(SectionRequestParseFailure.InvalidQuarter, result.Failure, nameof(TestSectionRequestParserInvalidQuarterFails));
        AssertEqual("BAD", result.InvalidQuarterValue, nameof(TestSectionRequestParserInvalidQuarterFails));
    }

    private static void TestNoIntentSnapshotReopen()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: false,
            boundaryRoundTripUsed: false,
            snapshotAvailable: true,
            buildTrace: string.Empty,
            autoCloseResumeAttempts: 0,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.ReopenWithSnapshot, decision.Action, nameof(TestNoIntentSnapshotReopen));
        AssertEqual(1, decision.NextAutoCloseResumeAttempts, nameof(TestNoIntentSnapshotReopen));
    }

    private static void TestNoIntentBoundaryRoundTripReopenWithoutSnapshot()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: false,
            boundaryRoundTripUsed: true,
            snapshotAvailable: false,
            buildTrace: string.Empty,
            autoCloseResumeAttempts: 1,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.ReopenWithoutSnapshot, decision.Action, nameof(TestNoIntentBoundaryRoundTripReopenWithoutSnapshot));
        AssertEqual(2, decision.NextAutoCloseResumeAttempts, nameof(TestNoIntentBoundaryRoundTripReopenWithoutSnapshot));
    }

    private static void TestNoIntentRecoveryExhaustedCancels()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: false,
            boundaryRoundTripUsed: false,
            snapshotAvailable: true,
            buildTrace: string.Empty,
            autoCloseResumeAttempts: 3,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.Cancel, decision.Action, nameof(TestNoIntentRecoveryExhaustedCancels));
        AssertEqual(true, decision.ShouldLogRecoveryExhausted, nameof(TestNoIntentRecoveryExhaustedCancels));
        AssertEqual(false, decision.ShouldLogSnapshotUnavailable, nameof(TestNoIntentRecoveryExhaustedCancels));
        AssertEqual(true, decision.ShouldLogClosedWithoutBuildAttempt, nameof(TestNoIntentRecoveryExhaustedCancels));
    }

    private static void TestNoIntentSnapshotUnavailableLogsBoundaryPrefix()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: false,
            boundaryRoundTripUsed: true,
            snapshotAvailable: false,
            buildTrace: string.Empty,
            autoCloseResumeAttempts: 3,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.Cancel, decision.Action, nameof(TestNoIntentSnapshotUnavailableLogsBoundaryPrefix));
        AssertEqual(true, decision.ShouldLogSnapshotUnavailable, nameof(TestNoIntentSnapshotUnavailableLogsBoundaryPrefix));
        AssertEqual(BoundaryUnavailablePrefix, decision.SnapshotUnavailablePrefix, nameof(TestNoIntentSnapshotUnavailableLogsBoundaryPrefix));
    }

    private static void TestBuildRequestedSnapshotRecovers()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: true,
            buildAttempted: true,
            boundaryRoundTripUsed: false,
            snapshotAvailable: true,
            buildTrace: "onbuild_success",
            autoCloseResumeAttempts: 2,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.RecoverSnapshot, decision.Action, nameof(TestBuildRequestedSnapshotRecovers));
        AssertEqual(0, decision.NextAutoCloseResumeAttempts, nameof(TestBuildRequestedSnapshotRecovers));
    }

    private static void TestBuildAttemptAbortedByValidationCancels()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: true,
            boundaryRoundTripUsed: false,
            snapshotAvailable: true,
            buildTrace: "onbuild_abort_requests_empty_or_invalid",
            autoCloseResumeAttempts: 0,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.Cancel, decision.Action, nameof(TestBuildAttemptAbortedByValidationCancels));
        AssertEqual(true, decision.ShouldLogFallbackUnavailable, nameof(TestBuildAttemptAbortedByValidationCancels));
    }

    private static void TestBuildAttemptNonValidationAbortRecovers()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: true,
            boundaryRoundTripUsed: false,
            snapshotAvailable: true,
            buildTrace: "build_button_click",
            autoCloseResumeAttempts: 0,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.RecoverSnapshot, decision.Action, nameof(TestBuildAttemptNonValidationAbortRecovers));
    }

    private static void TestExplicitCancelAlwaysCancels()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: true,
            buildRequested: true,
            buildAttempted: true,
            boundaryRoundTripUsed: false,
            snapshotAvailable: true,
            buildTrace: "onbuild_success",
            autoCloseResumeAttempts: 0,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.Cancel, decision.Action, nameof(TestExplicitCancelAlwaysCancels));
        AssertEqual(false, decision.ShouldLogFallbackUnavailable, nameof(TestExplicitCancelAlwaysCancels));
    }

    private static void TestBuildRequestedWithoutSnapshotCancels()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: true,
            buildAttempted: true,
            boundaryRoundTripUsed: false,
            snapshotAvailable: false,
            buildTrace: "onbuild_success",
            autoCloseResumeAttempts: 0,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.Cancel, decision.Action, nameof(TestBuildRequestedWithoutSnapshotCancels));
        AssertEqual(true, decision.ShouldLogFallbackUnavailable, nameof(TestBuildRequestedWithoutSnapshotCancels));
        AssertEqual(false, decision.ShouldLogClosedWithoutBuildAttempt, nameof(TestBuildRequestedWithoutSnapshotCancels));
    }

    private static void TestNoIntentSnapshotUnavailableUsesAutoClosePrefix()
    {
        var decision = UiSessionRecoveryDecisionEngine.EvaluateNoResult(
            explicitCancel: false,
            buildRequested: false,
            buildAttempted: false,
            boundaryRoundTripUsed: false,
            snapshotAvailable: false,
            buildTrace: string.Empty,
            autoCloseResumeAttempts: 0,
            maxAutoCloseResumeAttempts: 3);

        AssertEqual(UiNoResultAction.Cancel, decision.Action, nameof(TestNoIntentSnapshotUnavailableUsesAutoClosePrefix));
        AssertEqual(true, decision.ShouldLogSnapshotUnavailable, nameof(TestNoIntentSnapshotUnavailableUsesAutoClosePrefix));
        AssertEqual(AutoCloseUnavailablePrefix, decision.SnapshotUnavailablePrefix, nameof(TestNoIntentSnapshotUnavailableUsesAutoClosePrefix));
    }

    private static void TestBuildExecutionPlanDefaults()
    {
        var input = new AtsBuildInput();
        var plan = BuildExecutionPlan.Create(input, enableQuarterViewByEnvironment: false);

        AssertEqual(false, plan.ShowQuarterDefinitionLinework, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(true, plan.DrawQuarterViewForBuild, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(true, plan.EnableInternalQuarterDefinitionProcessing, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(false, plan.ShouldGenerateDispositionLabels, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(false, plan.ShouldBuildDispositionImportScope, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(false, plan.ShouldLoadQuartersForLabeling, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(false, plan.ShouldPlaceLabelsBeforePlsr, nameof(TestBuildExecutionPlanDefaults));
        AssertEqual(false, plan.ShouldImportDispositions(existingDispositionPolylineCount: 0), nameof(TestBuildExecutionPlanDefaults));
    }

    private static void TestQuarterVisibilityPolicyMatrix()
    {
        var offOff = QuarterVisibilityPolicy.Create(
            includeAtsFabric: false,
            allowMultiQuarterDispositions: false,
            enableQuarterViewByEnvironment: false);
        AssertEqual(false, offOff.ShowQuarterDefinitionView, nameof(TestQuarterVisibilityPolicyMatrix));
        AssertEqual(false, offOff.KeepQuarterHelperLinework, nameof(TestQuarterVisibilityPolicyMatrix));

        var toggleOnly = QuarterVisibilityPolicy.Create(
            includeAtsFabric: false,
            allowMultiQuarterDispositions: true,
            enableQuarterViewByEnvironment: false);
        AssertEqual(true, toggleOnly.ShowQuarterDefinitionView, nameof(TestQuarterVisibilityPolicyMatrix));
        AssertEqual(true, toggleOnly.KeepQuarterHelperLinework, nameof(TestQuarterVisibilityPolicyMatrix));

        var atsOnly = QuarterVisibilityPolicy.Create(
            includeAtsFabric: true,
            allowMultiQuarterDispositions: false,
            enableQuarterViewByEnvironment: false);
        AssertEqual(false, atsOnly.ShowQuarterDefinitionView, nameof(TestQuarterVisibilityPolicyMatrix));
        AssertEqual(true, atsOnly.KeepQuarterHelperLinework, nameof(TestQuarterVisibilityPolicyMatrix));

        var envOnly = QuarterVisibilityPolicy.Create(
            includeAtsFabric: false,
            allowMultiQuarterDispositions: false,
            enableQuarterViewByEnvironment: true);
        AssertEqual(true, envOnly.ShowQuarterDefinitionView, nameof(TestQuarterVisibilityPolicyMatrix));
        AssertEqual(true, envOnly.KeepQuarterHelperLinework, nameof(TestQuarterVisibilityPolicyMatrix));
    }

    private static void TestCleanupPlanMatrix()
    {
        var offOffInput = new AtsBuildInput
        {
            IncludeAtsFabric = false,
            AllowMultiQuarterDispositions = false,
            IncludeDispositionLinework = false,
        };
        var offOffVisibility = QuarterVisibilityPolicy.Create(
            includeAtsFabric: offOffInput.IncludeAtsFabric,
            allowMultiQuarterDispositions: offOffInput.AllowMultiQuarterDispositions,
            enableQuarterViewByEnvironment: false);
        var offOffPlan = CleanupPlan.Create(offOffInput, offOffVisibility);
        AssertEqual(true, offOffPlan.EraseQuarterDefinitionQuarterView, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseQuarterDefinitionHelperLines, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseQuarterBoxes, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseQuarterHelpers, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseSectionOutlines, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseContextSectionPieces, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseSectionLabels, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, offOffPlan.EraseDispositionLinework, nameof(TestCleanupPlanMatrix));

        var atsOnlyInput = new AtsBuildInput
        {
            IncludeAtsFabric = true,
            AllowMultiQuarterDispositions = false,
            IncludeDispositionLinework = false,
        };
        var atsOnlyVisibility = QuarterVisibilityPolicy.Create(
            includeAtsFabric: atsOnlyInput.IncludeAtsFabric,
            allowMultiQuarterDispositions: atsOnlyInput.AllowMultiQuarterDispositions,
            enableQuarterViewByEnvironment: false);
        var atsOnlyPlan = CleanupPlan.Create(atsOnlyInput, atsOnlyVisibility);
        AssertEqual(true, atsOnlyPlan.EraseQuarterDefinitionQuarterView, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, atsOnlyPlan.EraseQuarterDefinitionHelperLines, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, atsOnlyPlan.EraseQuarterBoxes, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, atsOnlyPlan.EraseQuarterHelpers, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, atsOnlyPlan.EraseSectionOutlines, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, atsOnlyPlan.EraseContextSectionPieces, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, atsOnlyPlan.EraseSectionLabels, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, atsOnlyPlan.EraseDispositionLinework, nameof(TestCleanupPlanMatrix));

        var fullVisibleInput = new AtsBuildInput
        {
            IncludeAtsFabric = true,
            AllowMultiQuarterDispositions = true,
            IncludeDispositionLinework = true,
        };
        var fullVisibleVisibility = QuarterVisibilityPolicy.Create(
            includeAtsFabric: fullVisibleInput.IncludeAtsFabric,
            allowMultiQuarterDispositions: fullVisibleInput.AllowMultiQuarterDispositions,
            enableQuarterViewByEnvironment: false);
        var fullVisiblePlan = CleanupPlan.Create(fullVisibleInput, fullVisibleVisibility);
        AssertEqual(false, fullVisiblePlan.EraseQuarterDefinitionQuarterView, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, fullVisiblePlan.EraseQuarterDefinitionHelperLines, nameof(TestCleanupPlanMatrix));
        AssertEqual(true, fullVisiblePlan.EraseQuarterBoxes, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, fullVisiblePlan.EraseQuarterHelpers, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, fullVisiblePlan.EraseSectionOutlines, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, fullVisiblePlan.EraseContextSectionPieces, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, fullVisiblePlan.EraseSectionLabels, nameof(TestCleanupPlanMatrix));
        AssertEqual(false, fullVisiblePlan.EraseDispositionLinework, nameof(TestCleanupPlanMatrix));
    }

    private static void TestBuildExecutionPlanQuarterVisibility()
    {
        var uiEnabledInput = new AtsBuildInput
        {
            AllowMultiQuarterDispositions = true,
        };
        var uiEnabledPlan = BuildExecutionPlan.Create(uiEnabledInput, enableQuarterViewByEnvironment: false);
        AssertEqual(true, uiEnabledPlan.ShowQuarterDefinitionLinework, nameof(TestBuildExecutionPlanQuarterVisibility));

        var envEnabledInput = new AtsBuildInput
        {
            AllowMultiQuarterDispositions = false,
        };
        var envEnabledPlan = BuildExecutionPlan.Create(envEnabledInput, enableQuarterViewByEnvironment: true);
        AssertEqual(true, envEnabledPlan.ShowQuarterDefinitionLinework, nameof(TestBuildExecutionPlanQuarterVisibility));

        var atsEnabledInput = new AtsBuildInput
        {
            IncludeAtsFabric = true,
            AllowMultiQuarterDispositions = false,
        };
        var atsEnabledPlan = BuildExecutionPlan.Create(atsEnabledInput, enableQuarterViewByEnvironment: false);
        AssertEqual(false, atsEnabledPlan.ShowQuarterDefinitionLinework, nameof(TestBuildExecutionPlanQuarterVisibility));
    }

    private static void TestBuildExecutionPlanPlsrDrivesScopeAndImport()
    {
        var input = new AtsBuildInput
        {
            CheckPlsr = true,
            IncludeDispositionLabels = false,
            IncludeDispositionLinework = false,
        };
        var plan = BuildExecutionPlan.Create(input, enableQuarterViewByEnvironment: false);

        AssertEqual(true, plan.ShouldGenerateDispositionLabels, nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(true, plan.ShouldBuildDispositionImportScope, nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(true, plan.ShouldLoadQuartersForLabeling, nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(false, plan.ShouldPlaceLabelsBeforePlsr, nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(true, plan.ShouldScanExistingDispositionPolylinesForPlsrFallback(dispositionImportScopeCount: 1), nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(false, plan.ShouldScanExistingDispositionPolylinesForPlsrFallback(dispositionImportScopeCount: 0), nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(true, plan.ShouldImportDispositions(existingDispositionPolylineCount: 0), nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
        AssertEqual(true, plan.ShouldImportDispositions(existingDispositionPolylineCount: 2), nameof(TestBuildExecutionPlanPlsrDrivesScopeAndImport));
    }

    private static void TestBuildExecutionPlanLabelPlacementOrdering()
    {
        var labelsOnlyInput = new AtsBuildInput
        {
            IncludeDispositionLabels = true,
            CheckPlsr = false,
        };
        var labelsOnlyPlan = BuildExecutionPlan.Create(labelsOnlyInput, enableQuarterViewByEnvironment: false);
        AssertEqual(true, labelsOnlyPlan.ShouldPlaceLabelsBeforePlsr, nameof(TestBuildExecutionPlanLabelPlacementOrdering));

        var labelsAndPlsrInput = new AtsBuildInput
        {
            IncludeDispositionLabels = true,
            CheckPlsr = true,
        };
        var labelsAndPlsrPlan = BuildExecutionPlan.Create(labelsAndPlsrInput, enableQuarterViewByEnvironment: false);
        AssertEqual(false, labelsAndPlsrPlan.ShouldPlaceLabelsBeforePlsr, nameof(TestBuildExecutionPlanLabelPlacementOrdering));
    }

    private static void TestBuildExecutionPlanPlsrMissingLabelPrecheckGate()
    {
        var input = new AtsBuildInput
        {
            CheckPlsr = true,
            IncludeDispositionLinework = false,
            IncludeDispositionLabels = false,
        };
        var plan = BuildExecutionPlan.Create(input, enableQuarterViewByEnvironment: false);

        var shouldImportDispositions = plan.ShouldImportDispositions(existingDispositionPolylineCount: 0);
        AssertEqual(true, shouldImportDispositions, nameof(TestBuildExecutionPlanPlsrMissingLabelPrecheckGate));
        AssertEqual(
            true,
            plan.ShouldRunPlsrMissingLabelPrecheck(shouldImportDispositions, existingDispositionPolylineCount: 0),
            nameof(TestBuildExecutionPlanPlsrMissingLabelPrecheckGate));
        AssertEqual(
            true,
            plan.ShouldRunPlsrMissingLabelPrecheck(shouldImportDispositions, existingDispositionPolylineCount: 1),
            nameof(TestBuildExecutionPlanPlsrMissingLabelPrecheckGate));
        AssertEqual(
            false,
            plan.ShouldRunPlsrMissingLabelPrecheck(shouldImportDispositions: false, existingDispositionPolylineCount: 0),
            nameof(TestBuildExecutionPlanPlsrMissingLabelPrecheckGate));
    }

    private static void TestBuildExecutionPlanSupplementalSectionInfoGate()
    {
        var input = new AtsBuildInput
        {
            IncludeDispositionLabels = true,
        };
        var plan = BuildExecutionPlan.Create(input, enableQuarterViewByEnvironment: false);

        AssertEqual(true, plan.ShouldLoadSupplementalSectionInfos(dispositionPolylineCount: 2), nameof(TestBuildExecutionPlanSupplementalSectionInfoGate));
        AssertEqual(false, plan.ShouldLoadSupplementalSectionInfos(dispositionPolylineCount: 0), nameof(TestBuildExecutionPlanSupplementalSectionInfoGate));
    }

    private static void TestBuildExecutionPlanPassThroughFlags()
    {
        var input = new AtsBuildInput
        {
            IncludeAtsFabric = true,
            DrawLsdSubdivisionLines = true,
            AutoCheckUpdateShapefilesAlways = true,
            IncludeP3Shapefiles = true,
            IncludeCompassMapping = true,
            IncludeCrownReservations = true,
            IncludeSurfaceImpact = true,
            IncludeQuarterSectionLabels = true,
        };
        var plan = BuildExecutionPlan.Create(input, enableQuarterViewByEnvironment: false);

        AssertEqual(true, plan.IncludeAtsFabric, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.DrawLsdSubdivisionLines, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.ShouldAutoUpdateShapes, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.ShouldImportP3Shapefiles, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.ShouldImportCompassMapping, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.ShouldImportCrownReservations, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.ShouldRunSurfaceImpact, nameof(TestBuildExecutionPlanPassThroughFlags));
        AssertEqual(true, plan.ShouldPlaceQuarterSectionLabels, nameof(TestBuildExecutionPlanPassThroughFlags));
    }

    private static void TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored()
    {
        var idUpdate = Guid.NewGuid();
        var idIgnored = Guid.NewGuid();
        var idCreateTemplate = Guid.NewGuid();

        var result = PlsrApplyDecisionEngine.Route(
            new[]
            {
                new PlsrApplyDecisionItem
                {
                    IssueId = idUpdate,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.UpdateOwner
                },
                new PlsrApplyDecisionItem
                {
                    IssueId = idIgnored,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.TagExpired
                },
                new PlsrApplyDecisionItem
                {
                    IssueId = idCreateTemplate,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.CreateMissingLabelFromTemplate
                }
            },
            new HashSet<Guid> { idUpdate, idCreateTemplate });

        AssertEqual(2, result.AcceptedActionable, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
        AssertEqual(1, result.IgnoredActionable, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
        AssertEqual(2, result.AcceptedRoutedIssues.Count, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
        AssertEqual(idUpdate, result.AcceptedRoutedIssues[0].IssueId, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
        AssertEqual(PlsrApplyDecisionActionType.UpdateOwner, result.AcceptedRoutedIssues[0].ActionType, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
        AssertEqual(idCreateTemplate, result.AcceptedRoutedIssues[1].IssueId, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
        AssertEqual(PlsrApplyDecisionActionType.CreateMissingLabelFromTemplate, result.AcceptedRoutedIssues[1].ActionType, nameof(TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored));
    }

    private static void TestPlsrApplyDecisionEnginePreservesAcceptedOrder()
    {
        var idCreate = Guid.NewGuid();
        var idUpdate = Guid.NewGuid();
        var idXml = Guid.NewGuid();

        var result = PlsrApplyDecisionEngine.Route(
            new[]
            {
                new PlsrApplyDecisionItem
                {
                    IssueId = idCreate,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.CreateMissingLabel
                },
                new PlsrApplyDecisionItem
                {
                    IssueId = idUpdate,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.UpdateOwner
                },
                new PlsrApplyDecisionItem
                {
                    IssueId = idXml,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.CreateMissingLabelFromXml
                }
            },
            new HashSet<Guid> { idCreate, idUpdate, idXml });

        AssertEqual(3, result.AcceptedActionable, nameof(TestPlsrApplyDecisionEnginePreservesAcceptedOrder));
        AssertEqual(0, result.IgnoredActionable, nameof(TestPlsrApplyDecisionEnginePreservesAcceptedOrder));
        AssertEqual(idCreate, result.AcceptedRoutedIssues[0].IssueId, nameof(TestPlsrApplyDecisionEnginePreservesAcceptedOrder));
        AssertEqual(idUpdate, result.AcceptedRoutedIssues[1].IssueId, nameof(TestPlsrApplyDecisionEnginePreservesAcceptedOrder));
        AssertEqual(idXml, result.AcceptedRoutedIssues[2].IssueId, nameof(TestPlsrApplyDecisionEnginePreservesAcceptedOrder));
    }

    private static void TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted()
    {
        var idNonActionable = Guid.NewGuid();
        var idAccepted = Guid.NewGuid();

        var result = PlsrApplyDecisionEngine.Route(
            new[]
            {
                new PlsrApplyDecisionItem
                {
                    IssueId = idNonActionable,
                    IsActionable = false,
                    ActionType = PlsrApplyDecisionActionType.CreateMissingLabel
                },
                new PlsrApplyDecisionItem
                {
                    IssueId = idAccepted,
                    IsActionable = true,
                    ActionType = PlsrApplyDecisionActionType.TagExpired
                }
            },
            new HashSet<Guid> { idNonActionable, idAccepted });

        AssertEqual(1, result.AcceptedActionable, nameof(TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted));
        AssertEqual(0, result.IgnoredActionable, nameof(TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted));
        AssertEqual(1, result.AcceptedRoutedIssues.Count, nameof(TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted));
        AssertEqual(idAccepted, result.AcceptedRoutedIssues[0].IssueId, nameof(TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted));
    }

    private static void TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes()
    {
        var result = PlsrMissingLabelCandidateSelector.Select(
            new PlsrMissingLabelCandidateSelectionInput
            {
                PreferredCandidateId = "B",
                IndexedCandidateIds = new[] { "A", "B", "C", "A" }
            });

        AssertEqual(true, result.HasCandidates, nameof(TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes));
        AssertEqual(3, result.OrderedCandidateIds.Count, nameof(TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes));
        AssertEqual("B", result.OrderedCandidateIds[0], nameof(TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes));
        AssertEqual("A", result.OrderedCandidateIds[1], nameof(TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes));
        AssertEqual("C", result.OrderedCandidateIds[2], nameof(TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes));
    }

    private static void TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred()
    {
        var result = PlsrMissingLabelCandidateSelector.Select(
            new PlsrMissingLabelCandidateSelectionInput
            {
                PreferredCandidateId = null,
                IndexedCandidateIds = new[] { "X", "Y", "Z" }
            });

        AssertEqual(true, result.HasCandidates, nameof(TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred));
        AssertEqual("X", result.OrderedCandidateIds[0], nameof(TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred));
        AssertEqual("Y", result.OrderedCandidateIds[1], nameof(TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred));
        AssertEqual("Z", result.OrderedCandidateIds[2], nameof(TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred));
    }

    private static void TestPlsrMissingLabelCandidateSelectorSkipsBlankCandidates()
    {
        var result = PlsrMissingLabelCandidateSelector.Select(
            new PlsrMissingLabelCandidateSelectionInput
            {
                PreferredCandidateId = " ",
                IndexedCandidateIds = new[] { "", "  ", "\t" }
            });

        AssertEqual(false, result.HasCandidates, nameof(TestPlsrMissingLabelCandidateSelectorSkipsBlankCandidates));
        AssertEqual(0, result.OrderedCandidateIds.Count, nameof(TestPlsrMissingLabelCandidateSelectorSkipsBlankCandidates));
    }

    private static void TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes()
    {
        var result = PlsrSummaryComposer.Compose(
            new PlsrSummaryComposeInput
            {
                IssueSummaryLines = new[] { "Issue B", "Issue A" },
                NotIncludedPrefixes = new[] { "z", "A", "m" },
                MissingLabels = 10,
                OwnerMismatches = 3,
                ExtraLabels = 4,
                ExpiredCandidates = 5,
                MissingCreated = 2,
                SkippedTextOnlyFallbackLabels = 1,
                OwnerUpdated = 6,
                ExpiredTagged = 7,
                AcceptedActionable = 8,
                IgnoredActionable = 9,
                ApplyErrors = 11,
                AllowTextOnlyFallbackLabels = false
            });

        AssertContains(result.SummaryText, "Issue B", nameof(TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes));
        AssertContains(result.SummaryText, "Issue A", nameof(TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes));
        AssertContains(result.SummaryText, "Not Included in check: A, m, z", nameof(TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes));
        AssertContains(result.SummaryText, "Missing labels: 10", nameof(TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes));
        AssertContains(result.SummaryText, "Apply errors: 11", nameof(TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes));
    }

    private static void TestPlsrSummaryComposerBuildsWarningWithSortedExamples()
    {
        var result = PlsrSummaryComposer.Compose(
            new PlsrSummaryComposeInput
            {
                SkippedTextOnlyFallbackLabels = 3,
                AllowTextOnlyFallbackLabels = false,
                SkippedTextOnlyFallbackExamples = new[]
                {
                    "PLA 2 in 1-1",
                    "LOC 1 in 1-1",
                    "DLO 4 in 1-1"
                }
            });

        AssertEqual(true, result.ShouldShowWarning, nameof(TestPlsrSummaryComposerBuildsWarningWithSortedExamples));
        AssertContains(result.WarningText, "Skipped labels:", nameof(TestPlsrSummaryComposerBuildsWarningWithSortedExamples));
        AssertContains(result.WarningText, " - DLO 4 in 1-1", nameof(TestPlsrSummaryComposerBuildsWarningWithSortedExamples));
        AssertContains(result.WarningText, " - LOC 1 in 1-1", nameof(TestPlsrSummaryComposerBuildsWarningWithSortedExamples));
        AssertContains(result.WarningText, " - PLA 2 in 1-1", nameof(TestPlsrSummaryComposerBuildsWarningWithSortedExamples));
    }

    private static void TestPlsrSummaryComposerSkipsWarningWhenTextFallbackAllowed()
    {
        var result = PlsrSummaryComposer.Compose(
            new PlsrSummaryComposeInput
            {
                SkippedTextOnlyFallbackLabels = 5,
                AllowTextOnlyFallbackLabels = true,
                SkippedTextOnlyFallbackExamples = new[] { "LOC 1 in 1-1" }
            });

        AssertEqual(false, result.ShouldShowWarning, nameof(TestPlsrSummaryComposerSkipsWarningWhenTextFallbackAllowed));
        AssertEqual(string.Empty, result.WarningText, nameof(TestPlsrSummaryComposerSkipsWarningWhenTextFallbackAllowed));
    }

    private static void AssertEqual<T>(T expected, T actual, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{testName}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void AssertContains(string text, string expectedSubstring, string testName)
    {
        if (text == null || expectedSubstring == null || text.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException($"{testName}: expected substring '{expectedSubstring}' was not found.");
        }
    }
}
