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
        TestQuarterBoundaryOwnershipPolicySurveyedSecFallback();
        TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership();
        TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset();
        TestSurveySecRoadAllowanceSecTargetPolicyAllowsPureSurveyZeroSecPair();
        TestSurveySecRoadAllowanceSecTargetPolicyRejectsProjectedUsecTarget();
        TestSurveySecRoadAllowanceSecTargetPolicyRejectsBlindSecFallback();
        TestCleanupPlanMatrix();
        TestBuildExecutionPlanDefaults();
        TestBuildExecutionPlanQuarterVisibility();
        TestBuildExecutionPlanPlsrDrivesScopeAndImport();
        TestBuildExecutionPlanLabelPlacementOrdering();
        TestBuildExecutionPlanPlsrMissingLabelPrecheckGate();
        TestBuildExecutionPlanSupplementalSectionInfoGate();
        TestBuildExecutionPlanPassThroughFlags();
        TestPerpendicularGapMeasurementMeasuresAngledFacingEdges();
        TestPerpendicularGapMeasurementHandlesReversedOtherEdgeDirection();
        TestPerpendicularLineDistanceMeasurementMeasuresAngledSignedOffset();
        TestPerpendicularLineBandMeasurementDetectsAngledStripIntersection();
        TestPerpendicularLineBandMeasurementRejectsSegmentOutsideAngledStrip();
        TestCorrectionBandAxisClassifierDetectsOuterAxisCorrectionZero();
        TestCorrectionBandAxisClassifierKeepsInnerAxisCorrectionZero();
        TestCorrectionBandAxisClassifierDetectsInnerAxisCorrectionOuter();
        TestCorrectionSouthBoundaryPreferencePrefersNearInsetBoundary();
        TestCorrectionSouthBoundaryPreferenceFallsBackToHardBoundaryWhenInsetMissing();
        TestCorrectionSouthBoundaryPreferenceRejectsImplausiblySmallInsetOffset();
        TestCorrectionSouthBoundaryPreferenceAllowsPlausibleInsetOffset();
        TestCorrectionSouthBoundaryPreferenceAllowsSmallUnlinkedDividerGap();
        TestCorrectionSouthBoundaryPreferenceRejectsLargeUnlinkedDividerGap();
        TestCorrectionSouthBoundaryPreferenceRejectsPartialHardBoundaryCoverage();
        TestCorrectionSouthBoundaryPreferenceAllowsFullHardBoundaryCoverage();
        TestCorrectionSouthBoundaryPreferenceRejectsPartialCompanionCoverage();
        TestCorrectionSouthBoundaryPreferenceAllowsFullCompanionCoverage();
        TestCorrectionSouthBoundaryPreferenceRejectsOppositeSideCompanionCandidate();
        TestCorrectionSouthBoundaryPreferenceAcceptsSameSideInsetCompanionCandidate();
        TestCorrectionOuterFallbackLayerClassifierIncludesOrdinaryUsec();
        TestCorrectionOuterFallbackLayerClassifierKeepsSouthZeroOnlyOnSouthSide();
        TestCorrectionOuterFallbackLayerClassifierIncludesOrdinaryUsecBridgeLayer();
        TestCorrectionOuterBridgePropagationPolicyRejectsOneSidedTouch();
        TestCorrectionOuterBridgePropagationPolicyAcceptsTwoSidedTouch();
        TestCorrectionOuterConsistencyPromotionPolicyRejectsOneSidedInsetCompanionPromotion();
        TestCorrectionOuterConsistencyPromotionPolicyAcceptsTwoSidedInsetCompanionPromotion();
        TestCorrectionOuterConsistencyPromotionPolicyAllowsPromotionWithoutInsetCompanion();
        TestCorrectionBoundaryTrendSamplingBuildsSampleFromRealBoundaryTrend();
        TestCorrectionBoundaryTrendSamplingRejectsVerticalBoundaryTrend();
        TestQuarterSouthBoundaryLayerFilterIncludesOrdinaryUsecFallbackLayer();
        TestQuarterSouthBoundaryLayerFilterExcludesCorrectionLayer();
        TestCorrectionZeroTargetPreferencePrefersNearestValidBand();
        TestCorrectionZeroTargetPreferenceUsesBoundaryGapAsTieBreaker();
        TestCorrectionZeroTargetPreferencePrefersInsetBandOverShorterMove();
        TestCorrectionZeroTargetPreferencePreservesPrimaryCorrectionBand();
        TestCorrectionZeroTargetPreferenceDoesNotFreezeNonCorrectionPrimaryBand();
        TestCorrectionZeroTargetPreferencePrefersCloserLiveSnapTarget();
        TestCorrectionZeroTargetPreferenceUsesMoveAsLiveSnapTieBreaker();
        TestCorrectionZeroTargetPreferenceAcceptsFirstEndpointAdjustmentCandidate();
        TestCorrectionZeroTargetPreferenceRejectsFartherEndpointAdjustmentCandidate();
        TestCorrectionZeroTargetPreferenceAcceptsCloserEndpointAdjustmentCandidate();
        TestCorrectionZeroCompanionProjectionPrefersNearbyCorrectionZeroTrend();
        TestCorrectionZeroCompanionProjectionAcceptsCurrentEndpointAsOrdinaryTarget();
        TestCorrectionZeroCompanionProjectionRejectsNonInsetOrdinaryTarget();
        TestBoundaryStationSpanPolicyRejectsFarExtrapolation();
        TestBoundaryStationSpanPolicyAllowsNearEndpointPad();
        TestCorrectionInsetGhostRowClassifierDetectsGhostTwentyRow();
        TestCorrectionInsetGhostRowClassifierCollectsConnectedGhostContinuation();
        TestCorrectionInsetGhostRowClassifierRejectsValidTwentyRow();
        TestCorrectionInsetGhostRowClassifierRejectsValidThirtyRow();
        TestOrdinaryUsecTieInAnchorLayerClassifierAcceptsUsec();
        TestOrdinaryUsecTieInAnchorLayerClassifierRejectsCorrection();
        TestSegmentStationProjectionPreservesProjectedStationOnAngledSegment();
        TestSegmentStationProjectionHandlesReversedSegmentDirection();
        TestSegmentStationProjectionRejectsStationOutsideSegmentSpan();

        TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored();
        TestPlsrApplyDecisionEnginePreservesAcceptedOrder();
        TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted();

        TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes();
        TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred();
        TestPlsrMissingLabelCandidateSelectorSkipsBlankCandidates();
        TestPlsrMissingLabelSuppressionPolicyMatchesNonStandardLayerCandidate();
        TestPlsrMissingLabelSuppressionPolicyRejectsDifferentQuarter();
        TestPlsrQuarterPointMatcherMatchesLeaderPointInsideWhenTextOutside();
        TestPlsrQuarterPointMatcherRejectsWhenAllPointsAreOutside();
        TestPlsrQuarterPointMatcherRejectsWhenPointIsInsideBoundsButOutsideQuarter();
        TestPlsrQuarterPointBuilderBuildsDimensionMidpointCandidate();
        TestPlsrQuarterPointBuilderDedupesRepeatedDimensionPoints();
        TestPlsrQuarterPointBuilderBuildsExtentCenterCandidate();
        TestPlsrQuarterTouchResolverReturnsAllTouchedQuarterKeys();
        TestPlsrQuarterTouchResolverPrefersHigherScoreForPrimaryQuarter();
        TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside();
        TestDispositionLabelColorPolicyForcesGreenForVariableWidthLabels();
        TestDispositionLabelColorPolicyPreservesRequestedColorForNonVariableLabels();

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

    private static void TestQuarterBoundaryOwnershipPolicySurveyedSecFallback()
    {
        const double secWidth = 20.11;
        const double usecWidth = 30.16;
        var policy = QuarterBoundaryOwnershipPolicy.Create(
            isBlindSouthBoundarySection: false,
            hasWestUsecZeroOwnershipCandidate: false,
            hasSouthUsecZeroOwnershipCandidate: false,
            roadAllowanceSecWidthMeters: secWidth,
            roadAllowanceUsecWidthMeters: usecWidth);

        AssertEqual(secWidth, policy.WestExpectedOffset, nameof(TestQuarterBoundaryOwnershipPolicySurveyedSecFallback));
        AssertEqual(secWidth, policy.SouthFallbackOffset, nameof(TestQuarterBoundaryOwnershipPolicySurveyedSecFallback));
        AssertEqual(true, policy.AllowWestInsetDowngrade, nameof(TestQuarterBoundaryOwnershipPolicySurveyedSecFallback));
        AssertEqual("fallback-20.12", policy.WestFallbackSource, nameof(TestQuarterBoundaryOwnershipPolicySurveyedSecFallback));
        AssertEqual("fallback-20.12", policy.SouthFallbackSource, nameof(TestQuarterBoundaryOwnershipPolicySurveyedSecFallback));
    }

    private static void TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership()
    {
        const double secWidth = 20.11;
        const double usecWidth = 30.16;
        var policy = QuarterBoundaryOwnershipPolicy.Create(
            isBlindSouthBoundarySection: false,
            hasWestUsecZeroOwnershipCandidate: true,
            hasSouthUsecZeroOwnershipCandidate: true,
            roadAllowanceSecWidthMeters: secWidth,
            roadAllowanceUsecWidthMeters: usecWidth);

        AssertEqual(usecWidth, policy.WestExpectedOffset, nameof(TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership));
        AssertEqual(usecWidth, policy.SouthFallbackOffset, nameof(TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership));
        AssertEqual(false, policy.AllowWestInsetDowngrade, nameof(TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership));
        AssertEqual("fallback-30.16", policy.WestFallbackSource, nameof(TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership));
        AssertEqual("fallback-30.16", policy.SouthFallbackSource, nameof(TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership));
    }

    private static void TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset()
    {
        const double secWidth = 20.11;
        const double usecWidth = 30.16;
        var policy = QuarterBoundaryOwnershipPolicy.Create(
            isBlindSouthBoundarySection: true,
            hasWestUsecZeroOwnershipCandidate: true,
            hasSouthUsecZeroOwnershipCandidate: true,
            roadAllowanceSecWidthMeters: secWidth,
            roadAllowanceUsecWidthMeters: usecWidth);

        AssertEqual(usecWidth, policy.WestExpectedOffset, nameof(TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset));
        AssertEqual(0.0, policy.SouthFallbackOffset, nameof(TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset));
        AssertEqual(false, policy.AllowWestInsetDowngrade, nameof(TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset));
        AssertEqual("fallback-30.16", policy.WestFallbackSource, nameof(TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset));
        AssertEqual("fallback-blind", policy.SouthFallbackSource, nameof(TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset));
    }

    private static void TestSurveySecRoadAllowanceSecTargetPolicyAllowsPureSurveyZeroSecPair()
    {
        var result = SurveySecRoadAllowanceSecTargetPolicy.ShouldUseSecTarget(
            new[] { "ZERO", "SEC" },
            hasProjectedZeroCandidate: false,
            hasProjectedTwentyCandidate: false,
            hasProjectedCorrectionZeroCandidate: false);

        AssertEqual(true, result, nameof(TestSurveySecRoadAllowanceSecTargetPolicyAllowsPureSurveyZeroSecPair));
    }

    private static void TestSurveySecRoadAllowanceSecTargetPolicyRejectsProjectedUsecTarget()
    {
        var result = SurveySecRoadAllowanceSecTargetPolicy.ShouldUseSecTarget(
            new[] { "TWENTY", "SEC" },
            hasProjectedZeroCandidate: false,
            hasProjectedTwentyCandidate: true,
            hasProjectedCorrectionZeroCandidate: false);

        AssertEqual(false, result, nameof(TestSurveySecRoadAllowanceSecTargetPolicyRejectsProjectedUsecTarget));
    }

    private static void TestSurveySecRoadAllowanceSecTargetPolicyRejectsBlindSecFallback()
    {
        var result = SurveySecRoadAllowanceSecTargetPolicy.ShouldUseSecTarget(
            new[] { "BLIND", "SEC" },
            hasProjectedZeroCandidate: false,
            hasProjectedTwentyCandidate: false,
            hasProjectedCorrectionZeroCandidate: false);

        AssertEqual(false, result, nameof(TestSurveySecRoadAllowanceSecTargetPolicyRejectsBlindSecFallback));
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
        AssertEqual(true, labelsAndPlsrPlan.ShouldPlaceLabelsBeforePlsr, nameof(TestBuildExecutionPlanLabelPlacementOrdering));
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

    private static void TestPerpendicularGapMeasurementMeasuresAngledFacingEdges()
    {
        const double expectedGap = 30.16;
        var baseStart = new GapPoint(0.0, 0.0);
        var baseEnd = new GapPoint(1000.0, 577.3502691896257);
        var normal = BuildUnitNormal(baseStart, baseEnd);
        var otherStart = new GapPoint(
            baseStart.X + (normal.X * expectedGap),
            baseStart.Y + (normal.Y * expectedGap));
        var otherEnd = new GapPoint(
            baseEnd.X + (normal.X * expectedGap),
            baseEnd.Y + (normal.Y * expectedGap));

        var result = PerpendicularGapMeasurement.TryMeasureBetweenFacingEdges(
            baseStart,
            baseEnd,
            otherStart,
            otherEnd,
            out var measuredGap);

        AssertEqual(true, result, nameof(TestPerpendicularGapMeasurementMeasuresAngledFacingEdges));
        AssertClose(expectedGap, measuredGap, 0.001, nameof(TestPerpendicularGapMeasurementMeasuresAngledFacingEdges));
    }

    private static void TestPerpendicularGapMeasurementHandlesReversedOtherEdgeDirection()
    {
        const double expectedGap = 20.11;
        var baseStart = new GapPoint(0.0, 0.0);
        var baseEnd = new GapPoint(900.0, 519.6152422706632);
        var normal = BuildUnitNormal(baseStart, baseEnd);
        var otherStart = new GapPoint(
            baseStart.X + (normal.X * expectedGap),
            baseStart.Y + (normal.Y * expectedGap));
        var otherEnd = new GapPoint(
            baseEnd.X + (normal.X * expectedGap),
            baseEnd.Y + (normal.Y * expectedGap));

        var result = PerpendicularGapMeasurement.TryMeasureBetweenFacingEdges(
            baseStart,
            baseEnd,
            otherEnd,
            otherStart,
            out var measuredGap);

        AssertEqual(true, result, nameof(TestPerpendicularGapMeasurementHandlesReversedOtherEdgeDirection));
        AssertClose(expectedGap, measuredGap, 0.001, nameof(TestPerpendicularGapMeasurementHandlesReversedOtherEdgeDirection));
    }

    private static void TestPerpendicularLineDistanceMeasurementMeasuresAngledSignedOffset()
    {
        const double expectedOffset = 30.16;
        var baseStart = new GapPoint(0.0, 0.0);
        var baseEnd = new GapPoint(1000.0, 577.3502691896257);
        var normal = BuildUnitNormal(baseStart, baseEnd);
        var point = new LineDistancePoint(
            baseStart.X + (normal.X * expectedOffset),
            baseStart.Y + (normal.Y * expectedOffset));
        var slope = (baseEnd.Y - baseStart.Y) / (baseEnd.X - baseStart.X);
        var intercept = baseStart.Y - (slope * baseStart.X);

        var measuredOffset = PerpendicularLineDistanceMeasurement.SignedDistanceToLine(point, slope, intercept);

        AssertClose(expectedOffset, measuredOffset, 0.001, nameof(TestPerpendicularLineDistanceMeasurementMeasuresAngledSignedOffset));
    }

    private static void TestPerpendicularLineBandMeasurementDetectsAngledStripIntersection()
    {
        const double northOffset = 30.16;
        var southStart = new GapPoint(0.0, 0.0);
        var southEnd = new GapPoint(1000.0, 577.3502691896257);
        var normal = BuildUnitNormal(southStart, southEnd);
        var slope = (southEnd.Y - southStart.Y) / (southEnd.X - southStart.X);
        var scale = Math.Sqrt(1.0 + (slope * slope));
        var southIntercept = 0.0;
        var northIntercept = northOffset * scale;
        var segmentA = new LineDistancePoint(
            southStart.X + (normal.X * -5.0),
            southStart.Y + (normal.Y * -5.0));
        var segmentB = new LineDistancePoint(
            southEnd.X + (normal.X * 5.0),
            southEnd.Y + (normal.Y * 5.0));

        var intersects = PerpendicularLineBandMeasurement.IntersectsStrip(
            segmentA,
            segmentB,
            slope,
            southIntercept,
            slope,
            northIntercept,
            tolerance: 0.0);

        AssertEqual(true, intersects, nameof(TestPerpendicularLineBandMeasurementDetectsAngledStripIntersection));
    }

    private static void TestPerpendicularLineBandMeasurementRejectsSegmentOutsideAngledStrip()
    {
        const double northOffset = 30.16;
        var southStart = new GapPoint(0.0, 0.0);
        var southEnd = new GapPoint(1000.0, 577.3502691896257);
        var normal = BuildUnitNormal(southStart, southEnd);
        var slope = (southEnd.Y - southStart.Y) / (southEnd.X - southStart.X);
        var scale = Math.Sqrt(1.0 + (slope * slope));
        var southIntercept = 0.0;
        var northIntercept = northOffset * scale;
        var segmentA = new LineDistancePoint(
            southStart.X + (normal.X * -18.0),
            southStart.Y + (normal.Y * -18.0));
        var segmentB = new LineDistancePoint(
            southEnd.X + (normal.X * -12.0),
            southEnd.Y + (normal.Y * -12.0));

        var intersects = PerpendicularLineBandMeasurement.IntersectsStrip(
            segmentA,
            segmentB,
            slope,
            southIntercept,
            slope,
            northIntercept,
            tolerance: 0.0);

        AssertEqual(false, intersects, nameof(TestPerpendicularLineBandMeasurementRejectsSegmentOutsideAngledStrip));
    }

    private static void TestCorrectionBandAxisClassifierDetectsOuterAxisCorrectionZero()
    {
        var isOuter = CorrectionBandAxisClassifier.IsCloserToOuterBand(
            centerDistanceFromSeam: 15.18,
            fullRoadAllowanceWidth: 30.18,
            insetFromOuterBand: 5.02);

        AssertEqual(true, isOuter, nameof(TestCorrectionBandAxisClassifierDetectsOuterAxisCorrectionZero));
    }

    private static void TestCorrectionBandAxisClassifierKeepsInnerAxisCorrectionZero()
    {
        var isOuter = CorrectionBandAxisClassifier.IsCloserToOuterBand(
            centerDistanceFromSeam: 10.07,
            fullRoadAllowanceWidth: 30.18,
            insetFromOuterBand: 5.02);

        AssertEqual(false, isOuter, nameof(TestCorrectionBandAxisClassifierKeepsInnerAxisCorrectionZero));
    }

    private static void TestCorrectionBandAxisClassifierDetectsInnerAxisCorrectionOuter()
    {
        var isInner = CorrectionBandAxisClassifier.IsCloserToInnerBand(
            centerDistanceFromSeam: 10.07,
            fullRoadAllowanceWidth: 30.18,
            insetFromOuterBand: 5.02);

        AssertEqual(true, isInner, nameof(TestCorrectionBandAxisClassifierDetectsInnerAxisCorrectionOuter));
    }

    private static void TestCorrectionSouthBoundaryPreferencePrefersNearInsetBoundary()
    {
        var prefersInset = CorrectionSouthBoundaryPreference.IsCloserToInsetThanHardBoundary(
            outwardDistanceFromSection: 5.4,
            correctionInsetMeters: 5.03,
            hardBoundaryMeters: 20.11);

        AssertEqual(true, prefersInset, nameof(TestCorrectionSouthBoundaryPreferencePrefersNearInsetBoundary));
    }

    private static void TestCorrectionSouthBoundaryPreferenceFallsBackToHardBoundaryWhenInsetMissing()
    {
        var prefersInset = CorrectionSouthBoundaryPreference.IsCloserToInsetThanHardBoundary(
            outwardDistanceFromSection: 25.2,
            correctionInsetMeters: 5.03,
            hardBoundaryMeters: 20.11);

        AssertEqual(false, prefersInset, nameof(TestCorrectionSouthBoundaryPreferenceFallsBackToHardBoundaryWhenInsetMissing));
    }

    private static void TestCorrectionSouthBoundaryPreferenceRejectsImplausiblySmallInsetOffset()
    {
        var plausible = CorrectionSouthBoundaryPreference.IsPlausibleInsetOffset(
            outwardDistanceFromSection: 3.69,
            correctionInsetMeters: 20.12);

        AssertEqual(false, plausible, nameof(TestCorrectionSouthBoundaryPreferenceRejectsImplausiblySmallInsetOffset));
    }

    private static void TestCorrectionSouthBoundaryPreferenceAllowsPlausibleInsetOffset()
    {
        var plausible = CorrectionSouthBoundaryPreference.IsPlausibleInsetOffset(
            outwardDistanceFromSection: 18.77,
            correctionInsetMeters: 20.12);

        AssertEqual(true, plausible, nameof(TestCorrectionSouthBoundaryPreferenceAllowsPlausibleInsetOffset));
    }

    private static void TestCorrectionSouthBoundaryPreferenceAllowsSmallUnlinkedDividerGap()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsUnlinkedDividerGapAcceptable(
            dividerGapMeters: 6.0,
            maxAllowedGapMeters: 12.0);

        AssertEqual(true, allowed, nameof(TestCorrectionSouthBoundaryPreferenceAllowsSmallUnlinkedDividerGap));
    }

    private static void TestCorrectionSouthBoundaryPreferenceRejectsLargeUnlinkedDividerGap()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsUnlinkedDividerGapAcceptable(
            dividerGapMeters: 79.138,
            maxAllowedGapMeters: 12.0);

        AssertEqual(false, allowed, nameof(TestCorrectionSouthBoundaryPreferenceRejectsLargeUnlinkedDividerGap));
    }

    private static void TestCorrectionSouthBoundaryPreferenceRejectsPartialHardBoundaryCoverage()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsHardBoundaryCoverageAcceptable(
            projectedOverlapMeters: 802.074,
            frameSpanMeters: 1636.0);

        AssertEqual(false, allowed, nameof(TestCorrectionSouthBoundaryPreferenceRejectsPartialHardBoundaryCoverage));
    }

    private static void TestCorrectionSouthBoundaryPreferenceAllowsFullHardBoundaryCoverage()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsHardBoundaryCoverageAcceptable(
            projectedOverlapMeters: 1300.0,
            frameSpanMeters: 1636.0);

        AssertEqual(true, allowed, nameof(TestCorrectionSouthBoundaryPreferenceAllowsFullHardBoundaryCoverage));
    }

    private static void TestCorrectionSouthBoundaryPreferenceRejectsPartialCompanionCoverage()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(
            projectedOverlapMeters: 802.074,
            sourceLengthMeters: 1636.0);

        AssertEqual(false, allowed, nameof(TestCorrectionSouthBoundaryPreferenceRejectsPartialCompanionCoverage));
    }

    private static void TestCorrectionSouthBoundaryPreferenceAllowsFullCompanionCoverage()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(
            projectedOverlapMeters: 1320.0,
            sourceLengthMeters: 1636.0);

        AssertEqual(true, allowed, nameof(TestCorrectionSouthBoundaryPreferenceAllowsFullCompanionCoverage));
    }

    private static void TestCorrectionSouthBoundaryPreferenceRejectsOppositeSideCompanionCandidate()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsSameSideInsetCompanionCandidate(
            outerSignedOffset: 17.9,
            candidateSignedOffset: -13.1,
            expectedInsetMeters: 5.02,
            toleranceMeters: 1.25);

        AssertEqual(false, allowed, nameof(TestCorrectionSouthBoundaryPreferenceRejectsOppositeSideCompanionCandidate));
    }

    private static void TestCorrectionSouthBoundaryPreferenceAcceptsSameSideInsetCompanionCandidate()
    {
        var allowed = CorrectionSouthBoundaryPreference.IsSameSideInsetCompanionCandidate(
            outerSignedOffset: 17.9,
            candidateSignedOffset: 13.1,
            expectedInsetMeters: 5.02,
            toleranceMeters: 1.25);

        AssertEqual(true, allowed, nameof(TestCorrectionSouthBoundaryPreferenceAcceptsSameSideInsetCompanionCandidate));
    }

    private static void TestCorrectionOuterFallbackLayerClassifierIncludesOrdinaryUsec()
    {
        var included = CorrectionOuterFallbackLayerClassifier.IsCorrectionOuterFallbackLayer("L-USEC", preferSouthSide: false);

        AssertEqual(true, included, nameof(TestCorrectionOuterFallbackLayerClassifierIncludesOrdinaryUsec));
    }

    private static void TestCorrectionOuterFallbackLayerClassifierKeepsSouthZeroOnlyOnSouthSide()
    {
        var southIncluded = CorrectionOuterFallbackLayerClassifier.IsCorrectionOuterFallbackLayer("L-USEC-0", preferSouthSide: true);
        var northIncluded = CorrectionOuterFallbackLayerClassifier.IsCorrectionOuterFallbackLayer("L-USEC-0", preferSouthSide: false);

        AssertEqual(true, southIncluded, nameof(TestCorrectionOuterFallbackLayerClassifierKeepsSouthZeroOnlyOnSouthSide) + "-south");
        AssertEqual(false, northIncluded, nameof(TestCorrectionOuterFallbackLayerClassifierKeepsSouthZeroOnlyOnSouthSide) + "-north");
    }

    private static void TestCorrectionOuterFallbackLayerClassifierIncludesOrdinaryUsecBridgeLayer()
    {
        var included = CorrectionOuterFallbackLayerClassifier.IsCorrectionOuterBridgeLayer("L-USEC");

        AssertEqual(true, included, nameof(TestCorrectionOuterFallbackLayerClassifierIncludesOrdinaryUsecBridgeLayer));
    }

    private static void TestCorrectionOuterBridgePropagationPolicyRejectsOneSidedTouch()
    {
        var startOnly = CorrectionOuterBridgePropagationPolicy.ShouldRelayerBridgeSegment(
            startTouchesCorrection: true,
            endTouchesCorrection: false);
        var endOnly = CorrectionOuterBridgePropagationPolicy.ShouldRelayerBridgeSegment(
            startTouchesCorrection: false,
            endTouchesCorrection: true);

        AssertEqual(false, startOnly, nameof(TestCorrectionOuterBridgePropagationPolicyRejectsOneSidedTouch) + "-start");
        AssertEqual(false, endOnly, nameof(TestCorrectionOuterBridgePropagationPolicyRejectsOneSidedTouch) + "-end");
    }

    private static void TestCorrectionOuterBridgePropagationPolicyAcceptsTwoSidedTouch()
    {
        var included = CorrectionOuterBridgePropagationPolicy.ShouldRelayerBridgeSegment(
            startTouchesCorrection: true,
            endTouchesCorrection: true);

        AssertEqual(true, included, nameof(TestCorrectionOuterBridgePropagationPolicyAcceptsTwoSidedTouch));
    }

    private static void TestCorrectionOuterConsistencyPromotionPolicyRejectsOneSidedInsetCompanionPromotion()
    {
        var promoted = CorrectionOuterConsistencyPromotionPolicy.ShouldPromoteSegment(
            startTouchesCorrectionChain: true,
            endTouchesCorrectionChain: false,
            hasParallelInsetCompanion: true);

        AssertEqual(false, promoted, nameof(TestCorrectionOuterConsistencyPromotionPolicyRejectsOneSidedInsetCompanionPromotion));
    }

    private static void TestCorrectionOuterConsistencyPromotionPolicyAcceptsTwoSidedInsetCompanionPromotion()
    {
        var promoted = CorrectionOuterConsistencyPromotionPolicy.ShouldPromoteSegment(
            startTouchesCorrectionChain: true,
            endTouchesCorrectionChain: true,
            hasParallelInsetCompanion: true);

        AssertEqual(true, promoted, nameof(TestCorrectionOuterConsistencyPromotionPolicyAcceptsTwoSidedInsetCompanionPromotion));
    }

    private static void TestCorrectionOuterConsistencyPromotionPolicyAllowsPromotionWithoutInsetCompanion()
    {
        var promoted = CorrectionOuterConsistencyPromotionPolicy.ShouldPromoteSegment(
            startTouchesCorrectionChain: false,
            endTouchesCorrectionChain: false,
            hasParallelInsetCompanion: false);

        AssertEqual(true, promoted, nameof(TestCorrectionOuterConsistencyPromotionPolicyAllowsPromotionWithoutInsetCompanion));
    }

    private static void TestCorrectionBoundaryTrendSamplingBuildsSampleFromRealBoundaryTrend()
    {
        var result = CorrectionBoundaryTrendSampling.TryBuildBoundarySampleAcrossXSpan(
            new LineDistancePoint(100.0, 200.0),
            new LineDistancePoint(20.0, 194.0),
            new LineDistancePoint(220.0, 209.0),
            minX: 50.0,
            maxX: 250.0,
            out var sampleA,
            out var sampleB);

        AssertEqual(true, result, nameof(TestCorrectionBoundaryTrendSamplingBuildsSampleFromRealBoundaryTrend));
        AssertClose(196.25, sampleA.Y, 0.001, nameof(TestCorrectionBoundaryTrendSamplingBuildsSampleFromRealBoundaryTrend) + "-A");
        AssertClose(211.25, sampleB.Y, 0.001, nameof(TestCorrectionBoundaryTrendSamplingBuildsSampleFromRealBoundaryTrend) + "-B");
    }

    private static void TestCorrectionBoundaryTrendSamplingRejectsVerticalBoundaryTrend()
    {
        var result = CorrectionBoundaryTrendSampling.TryBuildBoundarySampleAcrossXSpan(
            new LineDistancePoint(100.0, 200.0),
            new LineDistancePoint(90.0, 180.0),
            new LineDistancePoint(90.0, 220.0),
            minX: 50.0,
            maxX: 250.0,
            out _,
            out _);

        AssertEqual(false, result, nameof(TestCorrectionBoundaryTrendSamplingRejectsVerticalBoundaryTrend));
    }

    private static void TestQuarterSouthBoundaryLayerFilterIncludesOrdinaryUsecFallbackLayer()
    {
        var included = QuarterSouthBoundaryLayerFilter.IsOrdinaryResolutionLayer("L-USEC2012");

        AssertEqual(true, included, nameof(TestQuarterSouthBoundaryLayerFilterIncludesOrdinaryUsecFallbackLayer));
    }

    private static void TestQuarterSouthBoundaryLayerFilterExcludesCorrectionLayer()
    {
        var included = QuarterSouthBoundaryLayerFilter.IsOrdinaryResolutionLayer("L-USEC-C-0");

        AssertEqual(false, included, nameof(TestQuarterSouthBoundaryLayerFilterExcludesCorrectionLayer));
    }

    private static void TestCorrectionZeroTargetPreferencePrefersNearestValidBand()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterCandidate(
            moveDistance: 0.8,
            boundaryGap: 5.1,
            bestMoveDistance: 19.9,
            bestBoundaryGap: 0.3);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferencePrefersNearestValidBand));
    }

    private static void TestCorrectionZeroTargetPreferenceUsesBoundaryGapAsTieBreaker()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterCandidate(
            moveDistance: 1.0,
            boundaryGap: 4.8,
            bestMoveDistance: 1.0,
            bestBoundaryGap: 5.2);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferenceUsesBoundaryGapAsTieBreaker));
    }

    private static void TestCorrectionZeroTargetPreferencePrefersInsetBandOverShorterMove()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterInsetCandidate(
            targetOffsetError: 0.2,
            boundaryGap: 5.2,
            moveDistance: 6.0,
            bestTargetOffsetError: 14.8,
            bestBoundaryGap: 19.8,
            bestMoveDistance: 0.0);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferencePrefersInsetBandOverShorterMove));
    }

    private static void TestCorrectionZeroTargetPreferencePreservesPrimaryCorrectionBand()
    {
        var preserve = CorrectionZeroTargetPreference.ShouldPreserveExistingPrimaryBoundary("CORRZERO");

        AssertEqual(true, preserve, nameof(TestCorrectionZeroTargetPreferencePreservesPrimaryCorrectionBand));
    }

    private static void TestCorrectionZeroTargetPreferenceDoesNotFreezeNonCorrectionPrimaryBand()
    {
        var preserve = CorrectionZeroTargetPreference.ShouldPreserveExistingPrimaryBoundary("TWENTY");

        AssertEqual(false, preserve, nameof(TestCorrectionZeroTargetPreferenceDoesNotFreezeNonCorrectionPrimaryBand));
    }

    private static void TestCorrectionZeroTargetPreferencePrefersCloserLiveSnapTarget()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterLiveSnapCandidate(
            targetDelta: 0.12,
            moveDistance: 4.8,
            bestTargetDelta: 0.44,
            bestMoveDistance: 4.7);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferencePrefersCloserLiveSnapTarget));
    }

    private static void TestCorrectionZeroTargetPreferenceUsesMoveAsLiveSnapTieBreaker()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterLiveSnapCandidate(
            targetDelta: 0.18,
            moveDistance: 4.6,
            bestTargetDelta: 0.18,
            bestMoveDistance: 4.9);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferenceUsesMoveAsLiveSnapTieBreaker));
    }

    private static void TestCorrectionZeroTargetPreferenceAcceptsFirstEndpointAdjustmentCandidate()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterEndpointAdjustmentCandidate(
            hasExistingCandidate: false,
            candidateDistanceFromOriginal: 5.02,
            bestDistanceFromOriginal: double.MaxValue);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferenceAcceptsFirstEndpointAdjustmentCandidate));
    }

    private static void TestCorrectionZeroTargetPreferenceRejectsFartherEndpointAdjustmentCandidate()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterEndpointAdjustmentCandidate(
            hasExistingCandidate: true,
            candidateDistanceFromOriginal: 10.04,
            bestDistanceFromOriginal: 5.02);

        AssertEqual(false, isBetter, nameof(TestCorrectionZeroTargetPreferenceRejectsFartherEndpointAdjustmentCandidate));
    }

    private static void TestCorrectionZeroTargetPreferenceAcceptsCloserEndpointAdjustmentCandidate()
    {
        var isBetter = CorrectionZeroTargetPreference.IsBetterEndpointAdjustmentCandidate(
            hasExistingCandidate: true,
            candidateDistanceFromOriginal: 5.02,
            bestDistanceFromOriginal: 10.04);

        AssertEqual(true, isBetter, nameof(TestCorrectionZeroTargetPreferenceAcceptsCloserEndpointAdjustmentCandidate));
    }

    private static void TestCorrectionZeroCompanionProjectionPrefersNearbyCorrectionZeroTrend()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var projected = CorrectionZeroCompanionProjection.TryProjectCompanionTarget(
            endpoint: new LineDistancePoint(317438.874, 6033221.197),
            otherEndpoint: new LineDistancePoint(317437.479, 6033186.208),
            ordinaryTarget: new LineDistancePoint(317440.073, 6033251.323),
            correctionZeroRows,
            expectedInsetMeters: 5.02,
            insetToleranceMeters: 1.0,
            minExtendMeters: 0.05,
            maxExtendMeters: 45.0,
            out var correctionTarget);

        AssertEqual(true, projected, nameof(TestCorrectionZeroCompanionProjectionPrefersNearbyCorrectionZeroTrend) + "_Projected");
        AssertClose(317440.273, correctionTarget.X, 0.01, nameof(TestCorrectionZeroCompanionProjectionPrefersNearbyCorrectionZeroTrend) + "_X");
        AssertClose(6033256.340, correctionTarget.Y, 0.01, nameof(TestCorrectionZeroCompanionProjectionPrefersNearbyCorrectionZeroTrend) + "_Y");
    }

    private static void TestCorrectionZeroCompanionProjectionRejectsNonInsetOrdinaryTarget()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var projected = CorrectionZeroCompanionProjection.TryProjectCompanionTarget(
            endpoint: new LineDistancePoint(317438.874, 6033221.197),
            otherEndpoint: new LineDistancePoint(317437.479, 6033186.208),
            ordinaryTarget: new LineDistancePoint(317440.073, 6033244.900),
            correctionZeroRows,
            expectedInsetMeters: 5.02,
            insetToleranceMeters: 1.0,
            minExtendMeters: 0.05,
            maxExtendMeters: 45.0,
            out _);

        AssertEqual(false, projected, nameof(TestCorrectionZeroCompanionProjectionRejectsNonInsetOrdinaryTarget));
    }

    private static void TestCorrectionZeroCompanionProjectionAcceptsCurrentEndpointAsOrdinaryTarget()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var projected = CorrectionZeroCompanionProjection.TryProjectCompanionTarget(
            endpoint: new LineDistancePoint(317440.073, 6033251.323),
            otherEndpoint: new LineDistancePoint(317438.874, 6033221.197),
            ordinaryTarget: new LineDistancePoint(317440.073, 6033251.323),
            correctionZeroRows,
            expectedInsetMeters: 5.02,
            insetToleranceMeters: 1.0,
            minExtendMeters: 0.05,
            maxExtendMeters: 8.0,
            out var correctionTarget);

        AssertEqual(true, projected, nameof(TestCorrectionZeroCompanionProjectionAcceptsCurrentEndpointAsOrdinaryTarget) + "_Projected");
        AssertClose(317440.273, correctionTarget.X, 0.01, nameof(TestCorrectionZeroCompanionProjectionAcceptsCurrentEndpointAsOrdinaryTarget) + "_X");
        AssertClose(6033256.340, correctionTarget.Y, 0.01, nameof(TestCorrectionZeroCompanionProjectionAcceptsCurrentEndpointAsOrdinaryTarget) + "_Y");
    }

    private static void TestBoundaryStationSpanPolicyRejectsFarExtrapolation()
    {
        var within = BoundaryStationSpanPolicy.IsWithinSegmentSpan(
            station: 300.0,
            stationA: 800.0,
            stationB: 1600.0,
            pad: 25.0);

        AssertEqual(false, within, nameof(TestBoundaryStationSpanPolicyRejectsFarExtrapolation));
    }

    private static void TestBoundaryStationSpanPolicyAllowsNearEndpointPad()
    {
        var within = BoundaryStationSpanPolicy.IsWithinSegmentSpan(
            station: 778.0,
            stationA: 800.0,
            stationB: 1600.0,
            pad: 25.0);

        AssertEqual(true, within, nameof(TestBoundaryStationSpanPolicyAllowsNearEndpointPad));
    }

    private static void TestCorrectionInsetGhostRowClassifierDetectsGhostTwentyRow()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var detected = CorrectionInsetGhostRowClassifier.IsGhostRow(
            new LineDistancePoint(318248.606, 6033230.114),
            new LineDistancePoint(319056.732, 6033198.873),
            correctionZeroRows,
            expectedInsetMeters: 5.02);

        AssertEqual(true, detected, nameof(TestCorrectionInsetGhostRowClassifierDetectsGhostTwentyRow));
    }

    private static void TestCorrectionInsetGhostRowClassifierCollectsConnectedGhostContinuation()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var directGhost = CorrectionInsetGhostRowClassifier.IsGhostRow(
            new LineDistancePoint(317450.525, 6033260.968),
            new LineDistancePoint(318248.606, 6033230.114),
            correctionZeroRows,
            expectedInsetMeters: 5.02);
        var ghostChain = new HashSet<int>(CorrectionInsetGhostRowClassifier.FindGhostChainIndices(
            new List<(LineDistancePoint A, LineDistancePoint B)>
            {
                (new LineDistancePoint(318248.606, 6033230.114), new LineDistancePoint(319056.732, 6033198.873)),
                (new LineDistancePoint(317450.525, 6033260.968), new LineDistancePoint(318248.606, 6033230.114)),
                (new LineDistancePoint(318307.263, 6033196.91), new LineDistancePoint(317505.862, 6033229.796))
            },
            correctionZeroRows,
            expectedInsetMeters: 5.02));

        AssertEqual(false, directGhost, nameof(TestCorrectionInsetGhostRowClassifierCollectsConnectedGhostContinuation) + "_DirectSeedOnly");
        AssertEqual(true, ghostChain.Contains(0), nameof(TestCorrectionInsetGhostRowClassifierCollectsConnectedGhostContinuation) + "_Seed");
        AssertEqual(true, ghostChain.Contains(1), nameof(TestCorrectionInsetGhostRowClassifierCollectsConnectedGhostContinuation) + "_Continuation");
        AssertEqual(false, ghostChain.Contains(2), nameof(TestCorrectionInsetGhostRowClassifierCollectsConnectedGhostContinuation) + "_Ordinary");
    }

    private static void TestCorrectionInsetGhostRowClassifierRejectsValidTwentyRow()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var detected = CorrectionInsetGhostRowClassifier.IsGhostRow(
            new LineDistancePoint(318307.263, 6033196.91),
            new LineDistancePoint(317505.862, 6033229.796),
            correctionZeroRows,
            expectedInsetMeters: 5.02);

        AssertEqual(false, detected, nameof(TestCorrectionInsetGhostRowClassifierRejectsValidTwentyRow));
    }

    private static void TestCorrectionInsetGhostRowClassifierRejectsValidThirtyRow()
    {
        var correctionZeroRows = new List<(LineDistancePoint A, LineDistancePoint B)>
        {
            (new LineDistancePoint(318248.413, 6033225.098), new LineDistancePoint(319056.533, 6033193.856))
        };

        var detected = CorrectionInsetGhostRowClassifier.IsGhostRow(
            new LineDistancePoint(317505.862, 6033229.796),
            new LineDistancePoint(316714.504, 6033262.274),
            correctionZeroRows,
            expectedInsetMeters: 5.02);

        AssertEqual(false, detected, nameof(TestCorrectionInsetGhostRowClassifierRejectsValidThirtyRow));
    }

    private static void TestOrdinaryUsecTieInAnchorLayerClassifierAcceptsUsec()
    {
        var isAnchor = OrdinaryUsecTieInAnchorLayerClassifier.IsOuterUsecAnchorLayer("L-USEC");

        AssertEqual(true, isAnchor, nameof(TestOrdinaryUsecTieInAnchorLayerClassifierAcceptsUsec));
    }

    private static void TestOrdinaryUsecTieInAnchorLayerClassifierRejectsCorrection()
    {
        var isAnchor = OrdinaryUsecTieInAnchorLayerClassifier.IsOuterUsecAnchorLayer("L-USEC-C-0");

        AssertEqual(false, isAnchor, nameof(TestOrdinaryUsecTieInAnchorLayerClassifierRejectsCorrection));
    }

    private static void TestSegmentStationProjectionPreservesProjectedStationOnAngledSegment()
    {
        var resolved = SegmentStationProjection.TryResolvePointAtStation(
            new ProjectedStationPoint(0.0, 0.0),
            new ProjectedStationPoint(100.0, 25.0),
            new ProjectedStationVector(1.0, 0.0),
            40.0,
            0.01,
            out var point);

        AssertEqual(true, resolved, nameof(TestSegmentStationProjectionPreservesProjectedStationOnAngledSegment));
        AssertClose(40.0, point.X, 0.001, nameof(TestSegmentStationProjectionPreservesProjectedStationOnAngledSegment));
        AssertClose(10.0, point.Y, 0.001, nameof(TestSegmentStationProjectionPreservesProjectedStationOnAngledSegment));
    }

    private static void TestSegmentStationProjectionHandlesReversedSegmentDirection()
    {
        var resolved = SegmentStationProjection.TryResolvePointAtStation(
            new ProjectedStationPoint(100.0, 25.0),
            new ProjectedStationPoint(0.0, 0.0),
            new ProjectedStationVector(1.0, 0.0),
            40.0,
            0.01,
            out var point);

        AssertEqual(true, resolved, nameof(TestSegmentStationProjectionHandlesReversedSegmentDirection));
        AssertClose(40.0, point.X, 0.001, nameof(TestSegmentStationProjectionHandlesReversedSegmentDirection));
        AssertClose(10.0, point.Y, 0.001, nameof(TestSegmentStationProjectionHandlesReversedSegmentDirection));
    }

    private static void TestSegmentStationProjectionRejectsStationOutsideSegmentSpan()
    {
        var resolved = SegmentStationProjection.TryResolvePointAtStation(
            new ProjectedStationPoint(0.0, 0.0),
            new ProjectedStationPoint(100.0, 25.0),
            new ProjectedStationVector(1.0, 0.0),
            140.0,
            0.01,
            out _);

        AssertEqual(false, resolved, nameof(TestSegmentStationProjectionRejectsStationOutsideSegmentSpan));
    }

    private static (double X, double Y) BuildUnitNormal(GapPoint start, GapPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return (-dy / length, dx / length);
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

    private static void TestPlsrMissingLabelSuppressionPolicyMatchesNonStandardLayerCandidate()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PlsrMissingLabelSuppressionPolicy.BuildNonStandardLayerCandidateKey("5|18|57|7|NW", "DRS220054")
        };
        var result = PlsrMissingLabelSuppressionPolicy.ShouldSuppressMissingLabel(
            candidates,
            "5|18|57|7|NW",
            "DRS220054");
        AssertEqual(true, result, nameof(TestPlsrMissingLabelSuppressionPolicyMatchesNonStandardLayerCandidate));
    }
    private static void TestPlsrMissingLabelSuppressionPolicyRejectsDifferentQuarter()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PlsrMissingLabelSuppressionPolicy.BuildNonStandardLayerCandidateKey("5|18|57|7|NW", "DRS220054")
        };
        var result = PlsrMissingLabelSuppressionPolicy.ShouldSuppressMissingLabel(
            candidates,
            "5|18|57|7|SW",
            "DRS220054");
        AssertEqual(false, result, nameof(TestPlsrMissingLabelSuppressionPolicyRejectsDifferentQuarter));
    }
    private static void TestPlsrQuarterPointMatcherMatchesLeaderPointInsideWhenTextOutside()
    {
        var result = PlsrQuarterPointMatcher.MatchesAnyPoint(
            new PlsrQuarterMatchBounds(0.0, 0.0, 100.0, 100.0),
            new[]
            {
                new PlsrQuarterMatchPoint(130.0, 130.0),
                new PlsrQuarterMatchPoint(25.0, 25.0)
            },
            point => point.X == 25.0 && point.Y == 25.0);

        AssertEqual(true, result, nameof(TestPlsrQuarterPointMatcherMatchesLeaderPointInsideWhenTextOutside));
    }

    private static void TestPlsrQuarterPointMatcherRejectsWhenAllPointsAreOutside()
    {
        var result = PlsrQuarterPointMatcher.MatchesAnyPoint(
            new PlsrQuarterMatchBounds(0.0, 0.0, 100.0, 100.0),
            new[]
            {
                new PlsrQuarterMatchPoint(130.0, 130.0),
                new PlsrQuarterMatchPoint(-10.0, 25.0)
            },
            _ => true);

        AssertEqual(false, result, nameof(TestPlsrQuarterPointMatcherRejectsWhenAllPointsAreOutside));
    }

    private static void TestPlsrQuarterPointMatcherRejectsWhenPointIsInsideBoundsButOutsideQuarter()
    {
        var result = PlsrQuarterPointMatcher.MatchesAnyPoint(
            new PlsrQuarterMatchBounds(0.0, 0.0, 100.0, 100.0),
            new[]
            {
                new PlsrQuarterMatchPoint(50.0, 50.0)
            },
            _ => false);

        AssertEqual(false, result, nameof(TestPlsrQuarterPointMatcherRejectsWhenPointIsInsideBoundsButOutsideQuarter));
    }

    private static void TestPlsrQuarterPointBuilderBuildsDimensionMidpointCandidate()
    {
        var points = PlsrQuarterPointBuilder.BuildDimensionPoints(
            new PlsrQuarterMatchPoint(130.0, 130.0),
            new PlsrQuarterMatchPoint(130.0, 130.0),
            new PlsrQuarterMatchPoint(20.0, 40.0),
            new PlsrQuarterMatchPoint(30.0, 40.0));

        var result = PlsrQuarterPointMatcher.MatchesAnyPoint(
            new PlsrQuarterMatchBounds(0.0, 0.0, 100.0, 100.0),
            points,
            point => Math.Abs(point.X - 25.0) < 0.001 && Math.Abs(point.Y - 40.0) < 0.001);

        AssertEqual(true, result, nameof(TestPlsrQuarterPointBuilderBuildsDimensionMidpointCandidate));
    }

    private static void TestPlsrQuarterPointBuilderDedupesRepeatedDimensionPoints()
    {
        var points = PlsrQuarterPointBuilder.BuildDimensionPoints(
            new PlsrQuarterMatchPoint(25.0, 40.0),
            new PlsrQuarterMatchPoint(25.0, 40.0),
            new PlsrQuarterMatchPoint(20.0, 40.0),
            new PlsrQuarterMatchPoint(30.0, 40.0));

        AssertEqual(3, points.Count, nameof(TestPlsrQuarterPointBuilderDedupesRepeatedDimensionPoints));
    }

    private static void TestPlsrQuarterPointBuilderBuildsExtentCenterCandidate()
    {
        var points = PlsrQuarterPointBuilder.BuildExtentPoints(
            new PlsrQuarterMatchPoint(120.0, 50.0),
            new PlsrQuarterMatchPoint(-10.0, 40.0),
            new PlsrQuarterMatchPoint(110.0, 60.0));

        var result = PlsrQuarterPointMatcher.MatchesAnyPoint(
            new PlsrQuarterMatchBounds(0.0, 0.0, 100.0, 100.0),
            points,
            point => Math.Abs(point.X - 50.0) < 0.001 && Math.Abs(point.Y - 50.0) < 0.001);

        AssertEqual(true, result, nameof(TestPlsrQuarterPointBuilderBuildsExtentCenterCandidate));
    }
    private static void TestPlsrQuarterTouchResolverReturnsAllTouchedQuarterKeys()
    {
        var resolution = PlsrQuarterTouchResolver.Resolve(
            new[]
            {
                new PlsrQuarterTouchCandidate("5|19|57|12|NW", 1200, 10.0),
                new PlsrQuarterTouchCandidate("5|19|57|12|NE", 1100, 30.0),
                new PlsrQuarterTouchCandidate("5|19|57|12|NW", 1200, 12.0)
            });

        AssertEqual(2, resolution.TouchedQuarterKeys.Count, nameof(TestPlsrQuarterTouchResolverReturnsAllTouchedQuarterKeys));
        AssertEqual("5|19|57|12|NW", resolution.TouchedQuarterKeys[0], nameof(TestPlsrQuarterTouchResolverReturnsAllTouchedQuarterKeys));
        AssertEqual("5|19|57|12|NE", resolution.TouchedQuarterKeys[1], nameof(TestPlsrQuarterTouchResolverReturnsAllTouchedQuarterKeys));
        AssertEqual("5|19|57|12|NW", resolution.PrimaryQuarterKey, nameof(TestPlsrQuarterTouchResolverReturnsAllTouchedQuarterKeys));
    }

    private static void TestPlsrQuarterTouchResolverPrefersHigherScoreForPrimaryQuarter()
    {
        var resolution = PlsrQuarterTouchResolver.Resolve(
            new[]
            {
                new PlsrQuarterTouchCandidate("5|19|57|12|NW", 1001, 40.0),
                new PlsrQuarterTouchCandidate("5|19|57|12|NE", 1200, 5.0),
                new PlsrQuarterTouchCandidate("5|19|57|12|SW", 1200, 15.0)
            });

        AssertEqual(3, resolution.TouchedQuarterKeys.Count, nameof(TestPlsrQuarterTouchResolverPrefersHigherScoreForPrimaryQuarter));
        AssertEqual("5|19|57|12|SW", resolution.PrimaryQuarterKey, nameof(TestPlsrQuarterTouchResolverPrefersHigherScoreForPrimaryQuarter));
    }

    private static void TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside()
    {
        var longMultilineWidthLabel = "OWNER\\P10.00 m width required\\PLong multiline review text";
        var textHalfAlong = WidthAlignedDimensionPlacementPolicy.EstimateTextHalfAlong(
            longMultilineWidthLabel,
            textHeight: 2.5,
            pad: 0.875);
        var preferredOutsideAlong = WidthAlignedDimensionPlacementPolicy.GetPreferredOutsideAlongOffset(
            spanLength: 10.0,
            halfTextAlong: textHalfAlong,
            edgeMargin: 1.0);
        var alongOffsets = WidthAlignedDimensionPlacementPolicy.BuildSameLineAlongOffsets(
            spanLength: 10.0,
            preferredAlong: 0.0,
            halfTextAlong: textHalfAlong,
            edgeMargin: 1.0,
            step: 5.0,
            expansionCount: 3);
        var placement = WidthAlignedDimensionPlacementPolicy.Resolve(
            new WidthDimensionPoint(0.0, 0.0),
            new WidthDimensionPoint(10.0, 0.0),
            new WidthDimensionPoint(5.0 + alongOffsets[0], 6.0),
            dimLineOffset: 0.0);

        AssertEqual(3, longMultilineWidthLabel.Split(new[] { "\\P" }, StringSplitOptions.None).Length, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertEqual(true, Math.Abs(alongOffsets[0]) > 5.0, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(preferredOutsideAlong, Math.Abs(alongOffsets[0]), 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertEqual(true, placement.TextOutsideArrowSpan, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(5.0 + alongOffsets[0], placement.TextPoint.X, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(0.0, placement.TextPoint.Y, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(alongOffsets[0], placement.TextAlong, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(10.0, Math.Abs(placement.TextAlong) - 5.0 - textHalfAlong, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(0.0, placement.DimLineAlong, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(0.0, placement.DimLineOffset, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(5.0, placement.DimLinePoint.X, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
        AssertClose(0.0, placement.DimLinePoint.Y, 0.001, nameof(TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside));
    }

    private static void TestDispositionLabelColorPolicyForcesGreenForVariableWidthLabels()
    {
        var result = DispositionLabelColorPolicy.ResolveTextColorIndex("Owner\\PVariable Width\\PRoad\\P123", 256);

        AssertEqual(DispositionLabelColorPolicy.ReviewGreenColorIndex, result, nameof(TestDispositionLabelColorPolicyForcesGreenForVariableWidthLabels));
    }

    private static void TestDispositionLabelColorPolicyPreservesRequestedColorForNonVariableLabels()
    {
        var result = DispositionLabelColorPolicy.ResolveTextColorIndex("Owner\\P20.00 A/R\\P123", 256);

        AssertEqual(256, result, nameof(TestDispositionLabelColorPolicyPreservesRequestedColorForNonVariableLabels));
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

    private static void AssertClose(double expected, double actual, double tolerance, string testName)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{testName}: expected '{expected}' within '{tolerance}', actual '{actual}'.");
        }
    }
}
