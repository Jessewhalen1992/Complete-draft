using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private sealed class AtsBuildSessionContext
        {
            public AtsBuildSessionContext(
                string commandName,
                string inputSourceLabel,
                Editor editor,
                Database database,
                Logger logger,
                string dllFolder,
                Config config,
                ExcelLookup companyLookup,
                ExcelLookup purposeLookup,
                AtsBuildInput input,
                bool drawQuarterView,
                Action<string> setExitStage,
                Func<string> getExitStage,
                Action<string> emitExit)
            {
                CommandName = commandName ?? string.Empty;
                InputSourceLabel = inputSourceLabel ?? string.Empty;
                Editor = editor ?? throw new ArgumentNullException(nameof(editor));
                Database = database ?? throw new ArgumentNullException(nameof(database));
                Logger = logger ?? throw new ArgumentNullException(nameof(logger));
                DllFolder = dllFolder ?? string.Empty;
                Config = config ?? throw new ArgumentNullException(nameof(config));
                CompanyLookup = companyLookup ?? throw new ArgumentNullException(nameof(companyLookup));
                PurposeLookup = purposeLookup ?? throw new ArgumentNullException(nameof(purposeLookup));
                Input = input ?? throw new ArgumentNullException(nameof(input));
                DrawQuarterView = drawQuarterView;
                _setExitStage = setExitStage ?? throw new ArgumentNullException(nameof(setExitStage));
                _getExitStage = getExitStage ?? throw new ArgumentNullException(nameof(getExitStage));
                _emitExit = emitExit ?? throw new ArgumentNullException(nameof(emitExit));
            }

            private readonly Action<string> _setExitStage;
            private readonly Func<string> _getExitStage;
            private readonly Action<string> _emitExit;

            public string CommandName { get; }
            public string InputSourceLabel { get; }
            public Editor Editor { get; }
            public Database Database { get; }
            public Logger Logger { get; }
            public string DllFolder { get; }
            public Config Config { get; }
            public ExcelLookup CompanyLookup { get; }
            public ExcelLookup PurposeLookup { get; }
            public AtsBuildInput Input { get; }

            public bool DrawQuarterView { get; set; }
            public BuildExecutionPlan ExecutionPlan { get; set; } = null!;
            public SectionDrawResult SectionDrawResult { get; set; } = null!;
            public DispositionPreparationResult DispositionPreparation { get; set; } = null!;
            public string CurrentClient { get; set; } = string.Empty;
            public LayerManager LayerManager { get; set; } = null!;
            public SummaryResult Summary { get; } = new SummaryResult();
            public Dictionary<string, PlsrDispositionLabelOverride> PlsrLabelOverridesByDispNum { get; set; } =
                new Dictionary<string, PlsrDispositionLabelOverride>(StringComparer.OrdinalIgnoreCase);
            public List<DispositionInfo> Dispositions { get; set; } = new List<DispositionInfo>();
            public QuarterLabelContext QuarterLabelContext { get; set; } = null!;

            public void SetExitStage(string stage) => _setExitStage(stage);

            public string GetExitStage() => _getExitStage();

            public void EmitExit(string reason) => _emitExit(reason);

            public void WriteMessage(string message) => Editor.WriteMessage("\n" + message);
        }

        private static void LogAtsBuildSessionInput(AtsBuildSessionContext context)
        {
            var sectionSummary = BuildAtsBuildSectionSummary(context.Input.SectionRequests);
            context.Logger.WriteLine($"{context.CommandName} sections: {sectionSummary}");

            var selectedXmlCount = context.Input.PlsrXmlPaths?.Count ?? 0;
            context.Logger.WriteLine(
                context.InputSourceLabel +
                " options: CheckPLSR=" +
                (context.Input.CheckPlsr ? "ON" : "OFF") +
                ", SurfaceImpact=" +
                (context.Input.IncludeSurfaceImpact ? "ON" : "OFF") +
                ", XML files=" +
                selectedXmlCount +
                ".");
        }

        private static string BuildAtsBuildSectionSummary(IReadOnlyList<SectionRequest> sectionRequests)
        {
            const int sectionSummaryLimit = 12;
            if (sectionRequests == null || sectionRequests.Count == 0)
            {
                return "(none)";
            }

            var sectionRequestSummaries = sectionRequests
                .Take(sectionSummaryLimit)
                .Select(request => $"{request.Key.Zone}/{request.Key.Township}-{request.Key.Range}-{request.Key.Section} [{request.Quarter}]");
            var additionalSectionRequests = Math.Max(0, sectionRequests.Count - sectionSummaryLimit);
            return string.Join(", ", sectionRequestSummaries) +
                   (additionalSectionRequests > 0 ? $" +{additionalSectionRequests} more" : string.Empty);
        }

        private bool TryExecuteAtsBuildSession(AtsBuildSessionContext context)
        {
            InitializeAtsBuildSessionPlan(context);
            if (!TryPrepareAtsBuildSections(context))
            {
                return false;
            }

            PrepareAtsBuildDispositionInputs(context);
            if (!TryInitializeAtsBuildDispositionProcessing(context))
            {
                return false;
            }

            BuildAtsBuildQuarterLabelContext(context);
            ExecuteAtsBuildPostQuarterPipeline(context);
            return true;
        }

        private void InitializeAtsBuildSessionPlan(AtsBuildSessionContext context)
        {
            var executionPlan = BuildExecutionPlan.Create(context.Input, EnableQuarterViewByEnvironment);
            context.ExecutionPlan = executionPlan;
            context.DrawQuarterView = executionPlan.DrawQuarterViewForBuild;

            // Keep per-quarter disposition label/intersection logic enabled regardless of
            // whether quarter-definition linework is shown.
            context.Config.AllowMultiQuarterDispositions = executionPlan.EnableInternalQuarterDefinitionProcessing;
            context.Config.TextHeight = context.Input.TextHeight;
            context.Config.MaxOverlapAttempts = context.Input.MaxOverlapAttempts;

            context.Logger.WriteLine(
                $"Quarter view ({context.InputSourceLabel}): {(executionPlan.ShowQuarterDefinitionLinework ? "ON" : "OFF")} ({context.InputSourceLabel} 1/4 Definition linework={(context.Input.AllowMultiQuarterDispositions ? "ON" : "OFF")}, envOverride={(EnableQuarterViewByEnvironment ? "ON" : "OFF")}, internalBuild=ON).");
            context.Logger.WriteLine("Disposition 1/4 definition mode: ON (always enabled for per-quarter label logic).");

            if (executionPlan.ShouldAutoUpdateShapes)
            {
                AutoUpdateSelectedShapeSetsIfNeeded(context.Input, context.Config, context.Logger);
            }
        }

        private bool TryPrepareAtsBuildSections(AtsBuildSessionContext context)
        {
            if (!context.Config.UseSectionIndex)
            {
                return ExitAtsBuildSession(
                    context,
                    stage: "config_use_section_index_false",
                    message: "Config.UseSectionIndex=false. This build requires the section index workflow.",
                    exitReason: "error");
            }

            context.SetExitStage("sections_building");
            context.SectionDrawResult = DrawSectionsFromRequests(
                context.Editor,
                context.Database,
                context.Input.SectionRequests,
                context.Config,
                context.Logger,
                context.ExecutionPlan.DrawLsdSubdivisionLines,
                context.DrawQuarterView,
                context.ExecutionPlan.IncludeAtsFabric);
            context.SetExitStage("sections_built");

            if (context.SectionDrawResult.LabelQuarterPolylineIds.Count > 0)
            {
                return true;
            }

            context.SetExitStage("no_quarters_generated");
            context.WriteMessage("No quarter polylines generated from the section index (check your grid inputs).");

            // Ensure we don't leave temporary section outlines behind when ATS fabric is unchecked.
            CleanupAfterBuild(context.Database, context.SectionDrawResult, new List<ObjectId>(), context.Input, context.Logger);

            context.EmitExit("error");
            return false;
        }

        private void PrepareAtsBuildDispositionInputs(AtsBuildSessionContext context)
        {
            context.DispositionPreparation = PrepareDispositionInputs(
                context.Database,
                context.Editor,
                context.Logger,
                context.Config,
                context.Input,
                context.ExecutionPlan,
                context.SectionDrawResult,
                context.SetExitStage);
        }

        private bool TryInitializeAtsBuildDispositionProcessing(AtsBuildSessionContext context)
        {
            context.CurrentClient = context.Input.CurrentClient;
            if (string.IsNullOrWhiteSpace(context.CurrentClient))
            {
                return ExitAtsBuildSession(
                    context,
                    stage: "missing_current_client",
                    message: "Current client is required.",
                    exitReason: "error");
            }

            context.LayerManager = new LayerManager(context.Database);
            context.PlsrLabelOverridesByDispNum = context.ExecutionPlan.ShouldRunPlsrCheck
                ? BuildPlsrLabelOverridesByDispNum(context.Input.PlsrXmlPaths, context.CompanyLookup, context.Logger)
                : new Dictionary<string, PlsrDispositionLabelOverride>(StringComparer.OrdinalIgnoreCase);
            context.Dispositions = ProcessDispositionPolylines(
                context.Database,
                context.Editor,
                context.Logger,
                context.CompanyLookup,
                context.PurposeLookup,
                context.Config,
                context.Input,
                context.ExecutionPlan,
                context.SectionDrawResult,
                context.DispositionPreparation.DispositionPolylines,
                context.DispositionPreparation.ShouldGenerateDispositionLabels,
                context.CurrentClient,
                context.LayerManager,
                context.SetExitStage,
                context.Summary,
                context.PlsrLabelOverridesByDispNum);
            return true;
        }

        private void BuildAtsBuildQuarterLabelContext(AtsBuildSessionContext context)
        {
            context.QuarterLabelContext = BuildQuarterLabelContext(
                context.Database,
                context.Logger,
                context.SectionDrawResult,
                context.SectionDrawResult.LabelQuarterPolylineIds,
                context.ExecutionPlan.ShouldLoadQuartersForLabeling,
                context.DispositionPreparation.ShouldGenerateDispositionLabels);
        }

        private void ExecuteAtsBuildPostQuarterPipeline(AtsBuildSessionContext context)
        {
            ExecutePostQuarterPipeline(
                context.Database,
                context.Editor,
                context.Logger,
                context.CompanyLookup,
                context.Config,
                context.DllFolder,
                context.Input,
                context.ExecutionPlan,
                context.SectionDrawResult,
                context.DispositionPreparation.ImportedDispositionPolylines,
                context.DispositionPreparation.ImportSummary,
                context.CurrentClient,
                context.LayerManager,
                context.Dispositions,
                context.QuarterLabelContext,
                context.SetExitStage,
                context.Summary);
        }

        private static bool ExitAtsBuildSession(
            AtsBuildSessionContext context,
            string stage,
            string message,
            string exitReason)
        {
            context.SetExitStage(stage);
            context.WriteMessage(message);
            context.EmitExit(exitReason);
            return false;
        }
    }
}
