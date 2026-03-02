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

        TestBuildExecutionPlanDefaults();
        TestBuildExecutionPlanQuarterVisibility();
        TestBuildExecutionPlanPlsrDrivesScopeAndImport();
        TestBuildExecutionPlanLabelPlacementOrdering();
        TestBuildExecutionPlanPlsrMissingLabelPrecheckGate();
        TestBuildExecutionPlanSupplementalSectionInfoGate();
        TestBuildExecutionPlanPassThroughFlags();
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

    private static void AssertEqual<T>(T expected, T actual, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{testName}: expected '{expected}', actual '{actual}'.");
        }
    }
}
