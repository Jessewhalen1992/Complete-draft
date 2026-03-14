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
            public List<(bool Horizontal, double Axis, double SpanMin, double SpanMax)> OriginalRangeEdgeSecAnchors { get; set; } =
                new List<(bool Horizontal, double Axis, double SpanMin, double SpanMax)>();
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

            context.Logger?.WriteLine("Cleanup: section geometry finalized (SEC/QSEC/blind endpoint passes complete); deferred LSD draw begins.");
            if (context.DrawLsds)
            {
                DrawDeferredLsdSubdivisionLines(context.Database, context.LsdQuarterInfos.ToList(), context.Logger);
                EnforceLsdLineEndpointsOnHardSectionBoundaries(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    context.LsdQuarterInfos.ToList());
            }

            context.Logger?.WriteLine("Cleanup: final endpoint convergence pass begins (all endpoint targets recalculated from final geometry).");
            RunRoadAllowanceEndpointCleanup(context, includeLsdHardBoundaryPass: context.DrawLsds);
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
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            EnforceSectionLineNoCrossingRules(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            TrimZeroTwentyPassThroughExtensions(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            ResolveZeroTwentyOverlapByEndpointIntersection(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            CleanupOverlappingZeroTwentySectionLines(
                context.Database,
                context.RequestedScopeIds,
                context.Logger,
                context.ProtectedRoadAllowanceCleanupIds);
            EnforceSecLineEndpointsOnHardSectionBoundaries(context.Database, context.RequestedScopeIds, context.Logger);
            EnforceQuarterLineEndpointsOnSectionBoundaries(context.Database, context.RequestedScopeIds, context.Logger);
            EnforceBlindLineEndpointsOnSectionBoundaries(context.Database, context.RequestedScopeIds, context.Logger);

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
            var restoredNearbySectionIds = RestoreStashedSectionBuildingGeometry(
                context.Database,
                context.StashedNearbySectionEntities,
                context.Logger);
            if (context.KeepGeneratedAtsFabric)
            {
                var postRestoreCleanupWindows = ExpandClipWindows(
                    context.RequestedIsolationWindows,
                    PostRestoreOverlapCleanupExpansionMeters);
                CleanupOverlappingSectionLinesByShortest(
                    context.Database,
                    postRestoreCleanupWindows,
                    restoredNearbySectionIds,
                    context.Logger);
            }
            else
            {
                context.Logger?.WriteLine("Cleanup: skipped overlap shortest-wins on restored existing section segments (ATS fabric disabled).");
            }

            ApplyCorrectionLinePostBuildRules(
                context.Database,
                context.CorrectionLineSectionInfos,
                context.RequestedScopeIds,
                context.DrawLsds,
                context.Logger);

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

            EnforceFinalCorrectionOuterLayerConsistency(context.Database, context.RequestedScopeIds, context.Logger);
            NormalizeCorrectionLayerEntityColorByLayer(context.Database, context.Logger);
            if (context.DrawQuarterView)
            {
                DrawQuarterViewFromFinalRoadAllowanceGeometry(
                    context.Database,
                    context.SectionIds,
                    context.SectionNumberById,
                    context.Logger);
            }
            if (context.DrawLsds)
            {
                EnforceLsdLineEndpointsOnHardSectionBoundaries(
                    context.Database,
                    context.RequestedScopeIds,
                    context.Logger,
                    context.LsdQuarterInfos.ToList());
                RebuildLsdLabelsAtFinalIntersections(context.Database, context.LsdQuarterInfos.ToList(), context.Logger);
            }
            context.Logger?.WriteLine("Cleanup: final endpoint convergence pass complete.");
        }
    }
}

