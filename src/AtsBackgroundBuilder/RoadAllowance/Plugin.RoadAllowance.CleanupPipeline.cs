using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private sealed class RoadAllowanceCleanupContext
        {
            public Database Database { get; set; } = null!;
            public Logger Logger { get; set; } = null!;
            public IReadOnlyList<string> SearchFolders { get; set; } = Array.Empty<string>();
            public IReadOnlyList<SectionRequest> Requests { get; set; } = Array.Empty<SectionRequest>();
            public IReadOnlyCollection<ObjectId> RuleScopeIds { get; set; } = Array.Empty<ObjectId>();
            public IReadOnlyCollection<ObjectId> RequestedScopeIds { get; set; } = Array.Empty<ObjectId>();
            public IReadOnlyDictionary<string, string> InferredSecTypes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyDictionary<string, string> InferredQuarterSecTypes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyCollection<ObjectId> RequestedSectionBoundaryIds { get; set; } = Array.Empty<ObjectId>();
            public ICollection<ObjectId> ContextSectionIds { get; set; } = new List<ObjectId>();
            public IReadOnlyList<QuarterLabelInfo> ContextRuleSectionInfos { get; set; } = Array.Empty<QuarterLabelInfo>();
            public IReadOnlyList<QuarterLabelInfo> CorrectionContextSectionInfos { get; set; } = Array.Empty<QuarterLabelInfo>();
            public IReadOnlyList<QuarterLabelInfo> LabelQuarterInfos { get; set; } = Array.Empty<QuarterLabelInfo>();
            public IReadOnlyList<QuarterLabelInfo> LsdQuarterInfos { get; set; } = Array.Empty<QuarterLabelInfo>();
            public IReadOnlyDictionary<ObjectId, int> SectionNumberById { get; set; } = new Dictionary<ObjectId, int>();
            public HashSet<ObjectId> QuarterHelperIds { get; set; } = new HashSet<ObjectId>();
            public IList<Entity> StashedNearbySectionEntities { get; set; } = new List<Entity>();
            public IReadOnlyList<Extents3d> RequestedIsolationWindows { get; set; } = Array.Empty<Extents3d>();
            public IReadOnlyCollection<ObjectId> SectionIds { get; set; } = Array.Empty<ObjectId>();
            public bool DrawLsds { get; set; }
            public bool DrawQuarterView { get; set; }
            public bool KeepGeneratedAtsFabric { get; set; }

            public HashSet<ObjectId> GeneratedRoadAllowanceIds { get; } = new HashSet<ObjectId>();
            public HashSet<ObjectId> ProtectedRoadAllowanceCleanupIds { get; } = new HashSet<ObjectId>();
            public Dictionary<ObjectId, int> SectionNumberByPolylineIdForUsec { get; set; } = new Dictionary<ObjectId, int>();
            public List<QuarterLabelInfo> QuarterInfosForRoadAllowanceRules { get; set; } = new List<QuarterLabelInfo>();
            public List<QuarterLabelInfo> CorrectionLineSectionInfos { get; set; } = new List<QuarterLabelInfo>();
            public List<RangeEdgeAnchor> OriginalRangeEdgeSecAnchors { get; set; } =
                new List<RangeEdgeAnchor>();
        }

        private static void ExecuteRoadAllowanceCleanupPipeline(RoadAllowanceCleanupContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            GenerateRoadAllowanceGeometry(context);
            InitializeRoadAllowanceCleanupState(context);
            RunRoadAllowanceCanonicalCleanup(context);
            RunRoadAllowanceTrimAndDeterministicRelayer(context);
            RunRoadAllowanceEndpointCleanup(context);

            context.Logger?.WriteLine("Cleanup: section geometry finalized (SEC/QSEC/blind endpoint passes complete); LSD draw/enforcement remains deferred to the final stage.");

            context.Logger?.WriteLine("Cleanup: final endpoint convergence pass begins (all endpoint targets recalculated from final geometry).");
            RunRoadAllowanceEndpointCleanup(context, includeLsdHardBoundaryPass: false);
            FinalizeRoadAllowanceCleanup(context);
        }

        private static void GenerateRoadAllowanceGeometry(RoadAllowanceCleanupContext context)
        {
            var activeSectionKeyIds = new HashSet<string>(
                context.Requests.Select(r => BuildSectionKeyId(r.Key)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var contextInfo in context.ContextRuleSectionInfos)
            {
                if (contextInfo == null)
                {
                    continue;
                }

                activeSectionKeyIds.Add(BuildSectionKeyId(contextInfo.SectionKey));
            }

            foreach (var id in DrawRoadAllowanceGapOffsetLines(
                context.Database,
                context.SearchFolders,
                context.Requests,
                context.RuleScopeIds,
                context.InferredSecTypes,
                context.InferredQuarterSecTypes,
                context.Logger,
                activeSectionKeyIds))
            {
                context.QuarterHelperIds.Add(id);
                context.GeneratedRoadAllowanceIds.Add(id);
            }
        }

        private static void InitializeRoadAllowanceCleanupState(RoadAllowanceCleanupContext context)
        {
            context.Logger.WriteLine("Cleanup: canonical RA mode enabled; legacy RA extension/move passes are skipped.");
            CleanupGeneratedRoadAllowanceOverlaps(
                context.Database,
                context.GeneratedRoadAllowanceIds,
                context.Logger,
                allowEraseExisting: false,
                protectedExistingIds: context.RequestedSectionBoundaryIds);

            context.SectionNumberByPolylineIdForUsec = BuildRoadAllowanceSectionNumberByPolylineIdForUsec(
                context.SectionNumberById,
                context.ContextRuleSectionInfos);
            context.OriginalRangeEdgeSecAnchors = SnapshotOriginalRangeEdgeSecRoadAllowanceAnchors(
                context.Database,
                context.RuleScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "snapshot-original-range-edge-sec", context.Logger);

            context.QuarterInfosForRoadAllowanceRules = context.LabelQuarterInfos
                .Concat(context.ContextRuleSectionInfos)
                .Where(info => info != null)
                .ToList();
            context.CorrectionLineSectionInfos = context.LsdQuarterInfos
                .Concat(context.CorrectionContextSectionInfos)
                .Where(info => info != null)
                .ToList();
        }

        private static Dictionary<ObjectId, int> BuildRoadAllowanceSectionNumberByPolylineIdForUsec(
            IReadOnlyDictionary<ObjectId, int> sectionNumberById,
            IEnumerable<QuarterLabelInfo> contextRuleSectionInfos)
        {
            var sectionNumberByPolylineIdForUsec = new Dictionary<ObjectId, int>(sectionNumberById);
            foreach (var sectionInfo in contextRuleSectionInfos)
            {
                if (sectionInfo == null || sectionInfo.SectionPolylineId.IsNull)
                {
                    continue;
                }

                var sectionNumber = ParseSectionNumber(sectionInfo.SectionKey.Section);
                sectionNumberByPolylineIdForUsec[sectionInfo.SectionPolylineId] = sectionNumber;
            }

            return sectionNumberByPolylineIdForUsec;
        }

        private static void RunRoadAllowanceCanonicalCleanup(RoadAllowanceCleanupContext context)
        {
            NormalizeUsecLayersToThreeBands(
                context.Database,
                context.RuleScopeIds,
                context.SectionNumberByPolylineIdForUsec,
                context.GeneratedRoadAllowanceIds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-usec-three-bands-1", context.Logger);

            context.ProtectedRoadAllowanceCleanupIds.Clear();
            foreach (var protectedId in ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(
                         context.Database,
                         context.SearchFolders,
                         context.QuarterInfosForRoadAllowanceRules,
                         context.GeneratedRoadAllowanceIds,
                         context.Logger))
            {
                context.ProtectedRoadAllowanceCleanupIds.Add(protectedId);
            }

            if (context.ProtectedRoadAllowanceCleanupIds.Count > 0)
            {
                context.Logger?.WriteLine($"Cleanup: protecting {context.ProtectedRoadAllowanceCleanupIds.Count} SE-connected east-boundary line(s) from later generic 0/20 cleanup.");
            }

            CleanupDuplicateBlindLineSegments(context.Database, context.RuleScopeIds, context.Logger);
            context.Logger?.WriteLine("Cleanup: context 100m trim deferred to final geometry stage; context snap/stitch/seam-heal passes skipped in canonical RA mode.");

            NormalizeGeneratedRoadAllowanceLayers(context.Database, context.GeneratedRoadAllowanceIds, context.Logger);
            NormalizeShortRoadAllowanceLayersByNeighborhood(context.Database, context.RuleScopeIds, context.GeneratedRoadAllowanceIds, context.Logger);
            NormalizeHorizontalSecRoadAllowanceLayers(context.Database, context.RuleScopeIds, context.GeneratedRoadAllowanceIds, context.Logger);
            NormalizeBottomTownshipBoundaryLayers(
                context.Database,
                context.RuleScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.QuarterInfosForRoadAllowanceRules,
                context.Logger);
            NormalizeThirtyEighteenCorridorLayers(context.Database, context.RuleScopeIds, context.Logger);

            if (EnableRangeEdgeRelayer)
            {
                context.Logger?.WriteLine("Cleanup: range-edge relayer enabled; running duplicate cleanup pass.");
                CleanupDuplicateBlindLineSegments(context.Database, context.RuleScopeIds, context.Logger);
            }
            else
            {
                context.Logger?.WriteLine(
                    "Cleanup: range-edge relayer intentionally disabled pending deterministic fix for " +
                    "R/A layer mix/match on perpendicular township/range boundaries.");
            }

            context.Logger?.WriteLine("Cleanup: legacy SW/NW/simple-west/stop-rule passes skipped in canonical RA mode; SE east-boundary bridge pass enabled.");
            NormalizeBlindLineLayersBySecConnections(context.Database, context.RuleScopeIds, context.Logger);
            NormalizeUsecLayersToThreeBands(
                context.Database,
                context.RuleScopeIds,
                context.SectionNumberByPolylineIdForUsec,
                context.GeneratedRoadAllowanceIds,
                context.Logger);
            NormalizeUsecCollinearComponentLayerConsistency(
                context.Database,
                context.RuleScopeIds,
                context.SectionNumberByPolylineIdForUsec,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-usec-collinear-consistency", context.Logger);
            NormalizeWestRoadAllowanceBandsForKnownSections(
                context.Database,
                context.RuleScopeIds,
                context.SectionNumberByPolylineIdForUsec,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-west-ra-bands-1", context.Logger);
            NormalizeUsecLayersBySectionEdgeOffsets(
                context.Database,
                context.RuleScopeIds,
                context.SectionNumberByPolylineIdForUsec,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-section-edge-relayer-1", context.Logger);
            ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(
                context.Database,
                context.RuleScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.OriginalRangeEdgeSecAnchors,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-range-edge-reapply-1", context.Logger);
            context.Logger?.WriteLine("Cleanup: context endpoint snap/stitch disabled (context is build-adjoining + 100m trim only).");
        }

        private static void RunRoadAllowanceTrimAndDeterministicRelayer(RoadAllowanceCleanupContext context)
        {
            TrimContextSectionsToBufferedWindows(context.Database, context.ContextSectionIds, context.RequestedScopeIds, context.Logger);
            if (context.GeneratedRoadAllowanceIds.Count > 0)
            {
                TrimContextSectionsToBufferedWindows(
                    context.Database,
                    context.GeneratedRoadAllowanceIds.ToHashSet(),
                    context.RequestedScopeIds,
                    context.Logger,
                    protectRequestedCore: false);
                CleanupGeneratedRoadAllowanceOverlaps(
                    context.Database,
                    context.GeneratedRoadAllowanceIds,
                    context.Logger,
                    allowEraseExisting: true,
                    protectedExistingIds: context.RequestedSectionBoundaryIds);
            }

            ExtendSouthBoundarySwQuarterWestToNextUsec(
                context.Database,
                context.RequestedScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-sw-south-boundary-extension", context.Logger);
            ConnectDanglingUsecZeroTwentyEndpoints(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            NormalizeUsecLayersBySectionEdgeOffsets(
                context.Database,
                context.RuleScopeIds,
                context.SectionNumberByPolylineIdForUsec,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-section-edge-relayer-2", context.Logger);
            ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(
                context.Database,
                context.RuleScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.OriginalRangeEdgeSecAnchors,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RuleScopeIds, "after-range-edge-reapply-2", context.Logger);
            NormalizeBottomTownshipBoundaryLayers(
                context.Database,
                context.RuleScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.QuarterInfosForRoadAllowanceRules,
                context.Logger);
        }

        private static void RunRoadAllowanceEndpointCleanup(
            RoadAllowanceCleanupContext context,
            bool includeLsdHardBoundaryPass = false)
        {
            ConnectDanglingUsecZeroTwentyEndpoints(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-dangling-0-20-connect", context.Logger);
            // Keep endpoint ownership consistent with the canonical 0/20.11 rules.
            // Strict mode disables the blind-line fallback so this pass only snaps to
            // same-role hard targets, matching the prior section-definition fixes.
            ApplyCanonicalRoadAllowanceEndpointRules(
                context.Database,
                context.RequestedScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.Logger,
                allowBlindFallback: false);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-canonical-endpoint-rule", context.Logger);
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-overlap-cleanup-1", context.Logger);
            EnforceSectionLineNoCrossingRules(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-no-crossing", context.Logger);
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-overlap-cleanup-2", context.Logger);
            TrimZeroTwentyPassThroughExtensions(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-pass-through-trim", context.Logger);
            ResolveZeroTwentyOverlapByEndpointIntersection(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-overlap-endpoint-intersection", context.Logger);
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-overlap-cleanup-3", context.Logger);
            EnforceSecLineEndpointsOnHardSectionBoundaries(context.Database, context.RequestedScopeIds, context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-sec-endpoint-hard", context.Logger);
            EnforceQuarterLineEndpointsOnSectionBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-quarter-endpoint-hard", context.Logger);
            EnforceBlindLineEndpointsOnSectionBoundaries(context.Database, context.RequestedScopeIds, context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-blind-endpoint-hard", context.Logger);

            if (includeLsdHardBoundaryPass)
            {
                EnforceLsdLineEndpointsOnHardSectionBoundaries(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    context.LsdQuarterInfos.ToList());
            }
        }

        private static void FinalizeRoadAllowanceCleanup(RoadAllowanceCleanupContext context)
        {
            void TrimLateCorrectionSegments(string reason)
            {
                var lateCorrectionIds = CollectCorrectionLayerSegmentIdsForFinalTrim(
                    context.Database,
                    context.RequestedScopeIds);
                if (lateCorrectionIds.Count == 0)
                {
                    return;
                }

                context.Logger?.WriteLine(
                    $"Cleanup: final 100m trim considering {lateCorrectionIds.Count} late correction segment(s) {reason}.");
                TrimContextSectionsToBufferedWindows(
                    context.Database,
                    lateCorrectionIds,
                    context.RequestedScopeIds,
                    context.Logger,
                    protectRequestedCore: false);
            }

            void RestoreNearbySectionGeometry(bool runOverlapCleanup)
            {
                var restoredNearbySectionIds = RestoreStashedSectionBuildingGeometry(
                    context.Database,
                    context.StashedNearbySectionEntities,
                    context.Logger);
                if (!runOverlapCleanup)
                {
                    if (restoredNearbySectionIds.Count > 0)
                    {
                        context.Logger?.WriteLine("Cleanup: skipped overlap shortest-wins on restored existing section segments (ATS fabric disabled).");
                    }

                    return;
                }

                var postRestoreCleanupWindows = ExpandClipWindows(
                    context.RequestedIsolationWindows,
                    PostRestoreOverlapCleanupExpansionMeters);
                CleanupOverlappingSectionLinesByShortest(
                    context.Database,
                    postRestoreCleanupWindows,
                    restoredNearbySectionIds,
                    context.Logger);
            }

            if (context.KeepGeneratedAtsFabric)
            {
                RestoreNearbySectionGeometry(runOverlapCleanup: true);
            }
            else if (context.StashedNearbySectionEntities.Count > 0)
            {
                context.Logger?.WriteLine("Cleanup: ATS fabric disabled; delaying restore of stashed existing section segments until after generated correction-line cleanup.");
            }

            ApplyCorrectionLinePostBuildRules(
                context.Database,
                context.CorrectionLineSectionInfos,
                context.RequestedScopeIds,
                context.DrawLsds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-correction-postbuild-final", context.Logger);

            RestoreMixedNorthRoadAllowanceBands(
                context.Database,
                context.QuarterInfosForRoadAllowanceRules,
                context.GeneratedRoadAllowanceIds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-mixed-north-band-restore-final", context.Logger);

            var ordinaryUsecTieInsTrimmed = TrimOrdinaryUsecTieInOverhangsToVerticalBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            if (ordinaryUsecTieInsTrimmed)
            {
                context.Logger?.WriteLine("Cleanup: rerunning final 100m trim after ordinary USEC tie-in overhang trim.");
                context.Logger?.WriteLine("Cleanup: rerunning mixed north band restore after ordinary USEC tie-in trim.");
                RestoreMixedNorthRoadAllowanceBands(
                    context.Database,
                    context.QuarterInfosForRoadAllowanceRules,
                    context.GeneratedRoadAllowanceIds,
                    context.Logger);
            }
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-ordinary-usec-tiein-trim", context.Logger);

            context.Logger?.WriteLine("Cleanup: rerunning final 100m trim after correction-line post-processing.");
            TrimContextSectionsToBufferedWindows(
                context.Database,
                context.ContextSectionIds,
                context.RequestedScopeIds,
                context.Logger);
            if (context.GeneratedRoadAllowanceIds.Count > 0)
            {
                TrimContextSectionsToBufferedWindows(
                    context.Database,
                    context.GeneratedRoadAllowanceIds,
                    context.RequestedScopeIds,
                    context.Logger,
                    protectRequestedCore: false);
            }
            TrimLateCorrectionSegments("after correction-line post-processing.");
            context.Logger?.WriteLine("Cleanup: rerunning L-SEC hard-boundary rule after correction-line post-processing.");
            EnforceSecLineEndpointsOnHardSectionBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            TraceTargetLayerSegmentState(
                context.Database,
                context.RequestedScopeIds,
                "after-sec-endpoint-hard-post-correction-line-post",
                context.Logger);

            var finalCorrectionOuterConsistencyChanged =
                EnforceFinalCorrectionOuterLayerConsistency(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger);
            if (finalCorrectionOuterConsistencyChanged)
            {
                ConnectCorrectionInnerEndpointsToVerticalUsecBoundaries(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger);
                context.Logger?.WriteLine("Cleanup: rerunning final 100m trim after final correction outer consistency.");
                TrimContextSectionsToBufferedWindows(
                    context.Database,
                    context.ContextSectionIds,
                    context.RequestedScopeIds,
                    context.Logger);
                if (context.GeneratedRoadAllowanceIds.Count > 0)
                {
                    TrimContextSectionsToBufferedWindows(
                        context.Database,
                    context.GeneratedRoadAllowanceIds,
                    context.RequestedScopeIds,
                    context.Logger,
                    protectRequestedCore: false);
                }
                TrimLateCorrectionSegments("after final correction outer consistency.");
                context.Logger?.WriteLine("Cleanup: rerunning L-SEC hard-boundary rule after final correction outer consistency.");
                EnforceSecLineEndpointsOnHardSectionBoundaries(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger);
                TraceTargetLayerSegmentState(
                    context.Database,
                    context.RequestedScopeIds,
                    "after-sec-endpoint-hard-post-final-correction-consistency",
                    context.Logger);

            }
            var ordinaryUsecTieInsTrimmedAfterFinalTrim = TrimOrdinaryUsecTieInOverhangsToVerticalBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            if (ordinaryUsecTieInsTrimmedAfterFinalTrim)
            {
                context.Logger?.WriteLine("Cleanup: rerunning final 100m trim after final ordinary USEC tie-in overhang trim.");
                TrimContextSectionsToBufferedWindows(
                    context.Database,
                    context.ContextSectionIds,
                    context.RequestedScopeIds,
                    context.Logger);
                if (context.GeneratedRoadAllowanceIds.Count > 0)
                {
                    TrimContextSectionsToBufferedWindows(
                        context.Database,
                        context.GeneratedRoadAllowanceIds,
                        context.RequestedScopeIds,
                        context.Logger,
                        protectRequestedCore: false);
                }
                TrimLateCorrectionSegments("after final ordinary USEC tie-in overhang trim.");
                context.Logger?.WriteLine("Cleanup: rerunning mixed north band restore after final ordinary USEC tie-in trim.");
                RestoreMixedNorthRoadAllowanceBands(
                    context.Database,
                    context.QuarterInfosForRoadAllowanceRules,
                    context.GeneratedRoadAllowanceIds,
                    context.Logger);
            }
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-final-ordinary-usec-tiein-trim", context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-final-correction-outer-consistency", context.Logger);
            NormalizeCorrectionLayerEntityColorByLayer(context.Database, context.Logger);
            EnforceZeroTwentyEndpointsOnCorrectionZeroBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            TrimZeroTwentyVerticalOverhangsToHardHorizontalSections(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            var outerUsecTrimmedToCorrectionZero = TrimOuterUsecOvershootToCorrectionZeroBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            if (outerUsecTrimmedToCorrectionZero)
            {
                context.Logger?.WriteLine("Cleanup: rerunning final 100m trim after outer USEC correction-zero trim.");
                TrimContextSectionsToBufferedWindows(
                    context.Database,
                    context.ContextSectionIds,
                    context.RequestedScopeIds,
                    context.Logger);
                if (context.GeneratedRoadAllowanceIds.Count > 0)
                {
                    TrimContextSectionsToBufferedWindows(
                        context.Database,
                        context.GeneratedRoadAllowanceIds,
                        context.RequestedScopeIds,
                        context.Logger,
                        protectRequestedCore: false);
                }
                TrimLateCorrectionSegments("after outer USEC correction-zero trim.");
            }
            var correctionOuterBlindJoinTrimmed = TrimCorrectionOuterBlindJoinEndpointsToOrdinaryVerticalBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            if (correctionOuterBlindJoinTrimmed)
            {
                context.Logger?.WriteLine("Cleanup: correction outer blind-30 join trim adjusted final correction geometry.");
            }
            var mixedThirtyTwentyOrdinaryNormalized = NormalizeMixedThirtyTwentyOrdinaryEndpoints(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            if (mixedThirtyTwentyOrdinaryNormalized)
            {
                context.Logger?.WriteLine("Cleanup: mixed 30.16/20.11 ordinary endpoint normalize adjusted final road geometry.");
            }
            if (RestoreCorrectionLineBufferEndSpans(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: correction-line 100m buffer end-span restoration adjusted final road geometry.");
            }
            if (ProjectMisbandedTwentyRowsToTwentyOffset(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: projected misbanded 20.11 rows back to the 20.11 offset.");
            }
            if (TrimUsecTwentyEndpointsToZeroTerminators(
                    context.Database,
                    context.RequestedScopeIds,
                    context.GeneratedRoadAllowanceIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: trimmed 20.11 ordinary endpoints to zero/surveyed terminators.");
            }
            if (RepairMixedBandVerticalRoadAllowanceCompanions(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: repaired mixed-band vertical ordinary road companions.");
            }
            var innerBandRetargets = new List<Point2d>();
            var innerBandCapRetargets = new List<Point2d>();
            var innerBandEndpointRetargets = new List<(Point2d Original, Point2d Target)>();
            if (RetargetOrdinaryRoadAllowanceEndpointsToInnerBand(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    innerBandRetargets,
                    innerBandCapRetargets,
                    innerBandEndpointRetargets))
            {
                context.Logger?.WriteLine("Cleanup: retargeted ordinary road endpoints to the inner 20.11 band stop.");
            }
            if (ExtendShortOrdinaryRoadBoundaryStubsToAnchoredContinuation(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: extended short ordinary road-boundary stubs to anchored continuations.");
            }
            if (AddMissingShortTwentyBandCapsAtRoadCorners(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    innerBandCapRetargets))
            {
                context.Logger?.WriteLine("Cleanup: added missing short 20.11 road-corner cap line(s).");
            }
            if (TrimOrdinaryVerticalEndpointsToCorrectionOuterRows(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: trimmed ordinary vertical endpoints to correction outer rows.");
            }
            if (RepairMixedBandVerticalRoadAllowanceCompanions(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                context.Logger?.WriteLine("Cleanup: repaired mixed-band vertical ordinary road companions after retargeting.");
            }
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-late-correction-companion-snap", context.Logger);
            ExtendQuarterLinesFromUsecWestSouthToNextUsec(
                context.Database,
                context.RequestedScopeIds,
                context.GeneratedRoadAllowanceIds,
                context.Logger);
            TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-quarter-qsec-extension", context.Logger);
            if (RetargetVerticalQsecEndpointsToCorrectionZeroIntersections(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-quarter-qsec-correction-zero-retarget", context.Logger);
            }
            context.Logger?.WriteLine("Cleanup: rerunning 1/4 endpoint-on-section rule after final correction geometry and quarter extension.");
            EnforceQuarterLineEndpointsOnSectionBoundaries(
                context.Database,
                context.RequestedScopeIds,
                context.Logger);
            TraceTargetLayerSegmentState(
                context.Database,
                context.RequestedScopeIds,
                "after-quarter-endpoint-hard-post-final-correction",
                context.Logger);
            if (NormalizeSouthCorrectionQsecEndpoints(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger))
            {
                TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-south-correction-qsec-normalize", context.Logger);
            }
            if (context.DrawQuarterView)
            {
                DrawQuarterViewFromFinalRoadAllowanceGeometry(
                    context.Database,
                    context.SectionIds,
                    context.SectionNumberById,
                    context.Logger);
                if (RepairMixedBandVerticalRoadAllowanceCompanions(
                        context.Database,
                        context.RequestedScopeIds,
                        context.Logger))
                {
                    context.Logger?.WriteLine("Cleanup: repaired mixed-band vertical ordinary road companions after quarter view; redrawing quarter view.");
                    DrawQuarterViewFromFinalRoadAllowanceGeometry(
                        context.Database,
                        context.SectionIds,
                        context.SectionNumberById,
                        context.Logger);
                }
                if (RetargetQuarterDefinitionEndpointsToAdjustedInnerBand(
                        context.Database,
                        context.RequestedScopeIds,
                        context.Logger,
                        innerBandEndpointRetargets))
                {
                    context.Logger?.WriteLine("Cleanup: retargeted quarter endpoints to adjusted inner-band road stops.");
                }
            }
            if (context.DrawLsds)
            {
                DrawDeferredLsdSubdivisionLines(context.Database, context.LsdQuarterInfos.ToList(), context.Logger);
                EnforceLsdLineEndpointsOnHardSectionBoundaries(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    context.LsdQuarterInfos.ToList());
                RebuildLsdLabelsAtFinalIntersections(context.Database, context.LsdQuarterInfos.ToList(), context.Logger);
            }
            if (RetargetVerticalQsecEndpointsToCorrectionZeroIntersections(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    allowDeepJunctionRetargets: false))
            {
                TraceTargetLayerSegmentState(context.Database, context.RequestedScopeIds, "after-final-qsec-correction-zero-retarget", context.Logger);
            }
            if (!context.KeepGeneratedAtsFabric)
            {
                CaptureRemainingGeneratedRoadAllowanceEntityIds(context);
                RestoreNearbySectionGeometry(runOverlapCleanup: false);
            }

            context.Logger?.WriteLine("Cleanup: final endpoint convergence pass complete.");
        }

        private static void CaptureRemainingGeneratedRoadAllowanceEntityIds(RoadAllowanceCleanupContext context)
        {
            if (context?.Database == null || context.RequestedIsolationWindows == null || context.RequestedIsolationWindows.Count == 0)
            {
                return;
            }

            var captured = 0;
            using (var tr = context.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(context.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var isRoadAllowanceLayer =
                        IsUsecLayer(layer) ||
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                    if (!isRoadAllowanceLayer)
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!IntersectsAnyClipWindow(a, b, context.RequestedIsolationWindows))
                    {
                        continue;
                    }

                    if (context.GeneratedRoadAllowanceIds.Add(id))
                    {
                        captured++;
                    }
                }

                tr.Commit();
            }

            if (captured > 0)
            {
                context.Logger?.WriteLine($"Cleanup: captured {captured} remaining generated road allowance/correction line id(s) for final ATS-fabric-off cleanup.");
            }
        }

        private static List<ObjectId> CollectCorrectionLayerSegmentIdsForFinalTrim(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds)
        {
            var result = new List<ObjectId>();
            if (database == null || requestedQuarterIds == null)
            {
                return result;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return result;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var isCorrectionLayer =
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                    if (!isCorrectionLayer)
                    {
                        continue;
                    }

                    if (!TryReadOpenTwoPointSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!IntersectsAnyClipWindow(a, b, clipWindows))
                    {
                        continue;
                    }

                    result.Add(id);
                }
            }

            return result;
        }
    }
}

