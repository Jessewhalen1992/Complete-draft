using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using WinForms = System.Windows.Forms;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        [CommandMethod("ATSBUILD_XLS")]
        public void AtsBuildXls()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var database = doc.Database;

            var logger = new Logger();
            var dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
            logger.Initialize(Path.Combine(dllFolder, "AtsBackgroundBuilder.log"));

            var assemblyPath = Assembly.GetExecutingAssembly().Location ?? string.Empty;
            var assemblyLocalStamp = File.Exists(assemblyPath)
                ? File.GetLastWriteTime(assemblyPath)
                : DateTime.MinValue;
            var assemblyUtcStamp = File.Exists(assemblyPath)
                ? File.GetLastWriteTimeUtc(assemblyPath)
                : DateTime.MinValue;
            logger.WriteLine($"ATSBUILD_XLS assembly: {assemblyPath} (local={assemblyLocalStamp:yyyy-MM-dd h:mm:ss tt}, utc={assemblyUtcStamp:yyyy-MM-dd HH:mm:ss})");

            var rangeEdgeRelayerEnv = Environment.GetEnvironmentVariable("ATSBUILD_ENABLE_RANGE_EDGE_RELAYER");
            logger.WriteLine(
                $"ATSBUILD_XLS range-edge relayer env: {(string.IsNullOrWhiteSpace(rangeEdgeRelayerEnv) ? "<unset>" : rangeEdgeRelayerEnv)} (resolved {(EnableRangeEdgeRelayer ? "ON" : "OFF")}).");

            ClearBufferedDefpointsWindowsBeforeBuild(database, logger);

            var exitStage = "startup";
            void SetExitStage(string stage)
            {
                exitStage = stage;
                var stageMessage = "ATSBUILD_XLS stage: " + exitStage;
                logger.WriteLine(stageMessage);
                editor.WriteMessage("\n" + stageMessage);
            }

            logger.WriteLine("ATSBUILD_XLS stage: " + exitStage);
            editor.WriteMessage("\nATSBUILD_XLS stage: " + exitStage);

            void EmitExit(string reason)
            {
                var msg = $"ATSBUILD_XLS exit stage: {exitStage} ({reason})";
                editor.WriteMessage("\n" + msg);
                logger.WriteLine(msg);
            }

            try
            {
                var configPath = Path.Combine(dllFolder, "Config.json");
                var config = Config.Load(configPath, logger);
                var drawQuarterView = IsAffirmativeToggle(config.Quaterview) || EnableQuarterViewByEnvironment;
                logger.WriteLine($"Quarter view: {(drawQuarterView ? "ON" : "OFF")} (Config.Quaterview='{config.Quaterview}', envOverride={(EnableQuarterViewByEnvironment ? "ON" : "OFF")}).");

                var companyLookupPath = ResolveLookupPath(config.LookupFolder, config.CompanyLookupFile, dllFolder, "CompanyLookup.xlsx");
                var purposeLookupPath = ResolveLookupPath(config.LookupFolder, config.PurposeLookupFile, dllFolder, "PurposeLookup.xlsx");
                var companyLookup = ExcelLookup.Load(companyLookupPath, logger);
                var purposeLookup = ExcelLookup.Load(purposeLookupPath, logger);

                SetExitStage("prompt_workbook");
                var workbookPath = PromptForAtsBuildWorkbookPath(editor);
                if (string.IsNullOrWhiteSpace(workbookPath))
                {
                    SetExitStage("workbook_cancelled");
                    editor.WriteMessage("\nATSBUILD_XLS cancelled.");
                    EmitExit("cancelled");
                    logger.Dispose();
                    return;
                }

                SetExitStage("workbook_loading");
                var loadResult = AtsBuildExcelInputLoader.Load(workbookPath, logger);
                if (!loadResult.Success || loadResult.Input == null)
                {
                    SetExitStage("workbook_invalid");
                    var failureMessage = string.IsNullOrWhiteSpace(loadResult.ErrorMessage)
                        ? "Workbook could not be parsed."
                        : loadResult.ErrorMessage;
                    editor.WriteMessage("\nATSBUILD_XLS workbook error: " + failureMessage);
                    EmitExit("error");
                    logger.Dispose();
                    return;
                }

                editor.WriteMessage("\nATSBUILD_XLS workbook: " + loadResult.WorkbookPath);
                editor.WriteMessage(
                    $"\nATSBUILD_XLS parsed {loadResult.SourceRowCount} worksheet row(s) into {loadResult.Input.SectionRequests.Count} build request(s) from sheet '{loadResult.WorksheetName}'.");
                editor.WriteMessage(
                    $"\nATSBUILD_XLS client='{loadResult.Input.CurrentClient}', zone={loadResult.Input.Zone}, textHeight={loadResult.Input.TextHeight}, maxOverlapAttempts={loadResult.Input.MaxOverlapAttempts}.");

                ExecuteAtsBuildFromInput(
                    commandName: "ATSBUILD_XLS",
                    inputSourceLabel: "XLS",
                    editor: editor,
                    database: database,
                    logger: logger,
                    dllFolder: dllFolder,
                    config: config,
                    companyLookup: companyLookup,
                    purposeLookup: purposeLookup,
                    input: loadResult.Input,
                    drawQuarterView: drawQuarterView,
                    setExitStage: SetExitStage,
                    getExitStage: () => exitStage,
                    emitExit: EmitExit);
            }
            catch (System.Exception ex)
            {
                SetExitStage("startup_error");
                editor.WriteMessage("\nATSBUILD_XLS startup error: " + ex.Message);
                logger.WriteLine("ATSBUILD_XLS startup error: " + ex);
                EmitExit("error");
                logger.Dispose();
            }
        }

        private void ExecuteAtsBuildFromInput(
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
            if (input == null)
            {
                setExitStage("input_null");
                editor.WriteMessage($"\n{commandName} cancelled.");
                emitExit("cancelled");
                logger.Dispose();
                return;
            }

            const int sectionSummaryLimit = 12;
            string sectionSummary;
            if (input.SectionRequests.Count == 0)
            {
                sectionSummary = "(none)";
            }
            else
            {
                var sectionRequestSummaries = input.SectionRequests
                    .Take(sectionSummaryLimit)
                    .Select(request =>
                        $"{request.Key.Zone}/{request.Key.Township}-{request.Key.Range}-{request.Key.Section} [{request.Quarter}]");
                var additionalSectionRequests = Math.Max(0, input.SectionRequests.Count - sectionSummaryLimit);
                sectionSummary = string.Join(", ", sectionRequestSummaries) + (additionalSectionRequests > 0 ? $" +{additionalSectionRequests} more" : string.Empty);
            }

            logger.WriteLine($"{commandName} sections: {sectionSummary}");

            var selectedXmlCount = input.PlsrXmlPaths?.Count ?? 0;
            logger.WriteLine(
                inputSourceLabel +
                " options: CheckPLSR=" +
                (input.CheckPlsr ? "ON" : "OFF") +
                ", SurfaceImpact=" +
                (input.IncludeSurfaceImpact ? "ON" : "OFF") +
                ", XML files=" +
                selectedXmlCount +
                ".");

            try
            {
                var executionPlan = BuildExecutionPlan.Create(input, EnableQuarterViewByEnvironment);
                var showQuarterDefinitionLinework = executionPlan.ShowQuarterDefinitionLinework;
                drawQuarterView = executionPlan.DrawQuarterViewForBuild;

                // Keep per-quarter disposition label/intersection logic enabled regardless of
                // whether quarter-definition linework is shown.
                config.AllowMultiQuarterDispositions = executionPlan.EnableInternalQuarterDefinitionProcessing;
                config.TextHeight = input.TextHeight;
                config.MaxOverlapAttempts = input.MaxOverlapAttempts;

                logger.WriteLine(
                    $"Quarter view ({inputSourceLabel}): {(showQuarterDefinitionLinework ? "ON" : "OFF")} ({inputSourceLabel} 1/4 Definition linework={(input.AllowMultiQuarterDispositions ? "ON" : "OFF")}, envOverride={(EnableQuarterViewByEnvironment ? "ON" : "OFF")}, internalBuild=ON).");
                logger.WriteLine("Disposition 1/4 definition mode: ON (always enabled for per-quarter label logic).");

                if (executionPlan.ShouldAutoUpdateShapes)
                {
                    AutoUpdateSelectedShapeSetsIfNeeded(input, config, logger);
                }

                if (!config.UseSectionIndex)
                {
                    setExitStage("config_use_section_index_false");
                    editor.WriteMessage("\nConfig.UseSectionIndex=false. This build requires the section index workflow.");
                    emitExit("error");
                    logger.Dispose();
                    return;
                }

                // Build the section geometry used to determine 1/4s.
                // NOTE: These entities are considered "temporary" unless ATS fabric is enabled (see cleanup at the end).
                setExitStage("sections_building");
                var sectionDrawResult = DrawSectionsFromRequests(
                    editor,
                    database,
                    input.SectionRequests,
                    config,
                    logger,
                    executionPlan.DrawLsdSubdivisionLines,
                    drawQuarterView,
                    executionPlan.IncludeAtsFabric);
                setExitStage("sections_built");

                var quarterPolylinesForLabelling = sectionDrawResult.LabelQuarterPolylineIds;
                if (quarterPolylinesForLabelling.Count == 0)
                {
                    setExitStage("no_quarters_generated");
                    editor.WriteMessage("\nNo quarter polylines generated from the section index (check your grid inputs).");

                    // Ensure we don't leave temporary section outlines behind when ATS fabric is unchecked.
                    CleanupAfterBuild(database, sectionDrawResult, new List<ObjectId>(), input, logger);

                    emitExit("error");
                    logger.Dispose();
                    return;
                }

                var dispositionPreparation = PrepareDispositionInputs(
                    database,
                    editor,
                    logger,
                    config,
                    input,
                    executionPlan,
                    sectionDrawResult,
                    setExitStage);
                var shouldGenerateDispositionLabels = dispositionPreparation.ShouldGenerateDispositionLabels;
                var dispositionPolylines = dispositionPreparation.DispositionPolylines;
                var importedDispositionPolylines = dispositionPreparation.ImportedDispositionPolylines;
                var importSummary = dispositionPreparation.ImportSummary;

                var currentClient = input.CurrentClient;
                if (string.IsNullOrWhiteSpace(currentClient))
                {
                    setExitStage("missing_current_client");
                    editor.WriteMessage("\nCurrent client is required.");
                    emitExit("error");
                    logger.Dispose();
                    return;
                }

                var layerManager = new LayerManager(database);
                var result = new SummaryResult();
                var plsrLabelOverridesByDispNum = executionPlan.ShouldRunPlsrCheck
                    ? BuildPlsrLabelOverridesByDispNum(input.PlsrXmlPaths, companyLookup, logger)
                    : new Dictionary<string, PlsrDispositionLabelOverride>(StringComparer.OrdinalIgnoreCase);
                var dispositions = ProcessDispositionPolylines(
                    database,
                    editor,
                    logger,
                    companyLookup,
                    purposeLookup,
                    config,
                    input,
                    executionPlan,
                    sectionDrawResult,
                    dispositionPolylines,
                    shouldGenerateDispositionLabels,
                    currentClient,
                    layerManager,
                    setExitStage,
                    result,
                    plsrLabelOverridesByDispNum);

                var quarterLabelContext = BuildQuarterLabelContext(
                    database,
                    logger,
                    sectionDrawResult,
                    quarterPolylinesForLabelling,
                    executionPlan.ShouldLoadQuartersForLabeling,
                    shouldGenerateDispositionLabels);
                ExecutePostQuarterPipeline(
                    database,
                    editor,
                    logger,
                    companyLookup,
                    config,
                    dllFolder,
                    input,
                    executionPlan,
                    sectionDrawResult,
                    importedDispositionPolylines,
                    importSummary,
                    currentClient,
                    layerManager,
                    dispositions,
                    quarterLabelContext,
                    setExitStage,
                    result);
                setExitStage("completed");
                emitExit("ok");
                logger.Dispose();
            }
            catch (System.Exception ex)
            {
                setExitStage("fatal_exception");
                var failureText = $"{commandName} failed at stage '{getExitStage()}': {ex.Message}";
                logger.WriteLine(failureText);
                logger.WriteLine(ex.ToString());
                try
                {
                    editor.WriteMessage("\n" + failureText);
                }
                catch
                {
                    // Best-effort command line reporting only.
                }

                emitExit("error");
                logger.Dispose();
            }
        }

        private static string? PromptForAtsBuildWorkbookPath(Editor editor)
        {
            var prompt = new PromptStringOptions("\nEnter ATSBUILD workbook path or press Enter to browse")
            {
                AllowSpaces = true,
            };

            var promptResult = editor.GetString(prompt);
            if (promptResult.Status == PromptStatus.OK)
            {
                var path = promptResult.StringResult?.Trim();
                return string.IsNullOrWhiteSpace(path)
                    ? BrowseForAtsBuildWorkbookPath()
                    : path;
            }

            if (promptResult.Status == PromptStatus.None)
            {
                return BrowseForAtsBuildWorkbookPath();
            }

            return null;
        }

        private static string? BrowseForAtsBuildWorkbookPath()
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Filter = "Excel Workbook (*.xlsx;*.xls;*.xlsm;*.xlsb)|*.xlsx;*.xls;*.xlsm;*.xlsb|All files (*.*)|*.*",
                Title = "Select ATSBUILD input workbook",
                InitialDirectory = Environment.CurrentDirectory,
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
            };

            return dialog.ShowDialog() == WinForms.DialogResult.OK
                ? dialog.FileName
                : null;
        }
    }
}
