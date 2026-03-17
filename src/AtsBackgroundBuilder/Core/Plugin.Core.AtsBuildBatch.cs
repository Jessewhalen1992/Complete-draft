using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private const string AtsBuildBatchWorkbookEnvVar = "ATSBUILD_BATCH_WORKBOOK";
        private const string AtsBuildBatchDxfPathEnvVar = "ATSBUILD_BATCH_DXF_PATH";

        [CommandMethod("ATSBUILD_XLS_BATCH")]
        public void AtsBuildXlsBatch()
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
            logger.WriteLine($"ATSBUILD_XLS_BATCH assembly: {assemblyPath} (local={assemblyLocalStamp:yyyy-MM-dd h:mm:ss tt}, utc={assemblyUtcStamp:yyyy-MM-dd HH:mm:ss})");

            var rangeEdgeRelayerEnv = Environment.GetEnvironmentVariable("ATSBUILD_ENABLE_RANGE_EDGE_RELAYER");
            logger.WriteLine(
                $"ATSBUILD_XLS_BATCH range-edge relayer env: {(string.IsNullOrWhiteSpace(rangeEdgeRelayerEnv) ? "<unset>" : rangeEdgeRelayerEnv)} (resolved {(EnableRangeEdgeRelayer ? "ON" : "OFF")}).");

            ClearBufferedDefpointsWindowsBeforeBuild(database, logger);

            var exitStage = "startup";
            void SetExitStage(string stage)
            {
                exitStage = stage;
                var stageMessage = "ATSBUILD_XLS_BATCH stage: " + exitStage;
                logger.WriteLine(stageMessage);
                editor.WriteMessage("\n" + stageMessage);
            }

            logger.WriteLine("ATSBUILD_XLS_BATCH stage: " + exitStage);
            editor.WriteMessage("\nATSBUILD_XLS_BATCH stage: " + exitStage);

            void EmitExit(string reason)
            {
                var msg = $"ATSBUILD_XLS_BATCH exit stage: {exitStage} ({reason})";
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

                SetExitStage("resolve_workbook");
                var workbookPath = ResolveBatchWorkbookPath(editor);
                if (string.IsNullOrWhiteSpace(workbookPath))
                {
                    SetExitStage("workbook_cancelled");
                    editor.WriteMessage("\nATSBUILD_XLS_BATCH cancelled.");
                    EmitExit("cancelled");
                    logger.Dispose();
                    return;
                }

                var dxfPath = ResolveBatchDxfPath();
                if (!string.IsNullOrWhiteSpace(dxfPath))
                {
                    logger.WriteLine("ATSBUILD_XLS_BATCH dxf target: " + dxfPath);
                }

                SetExitStage("workbook_loading");
                var loadResult = AtsBuildExcelInputLoader.Load(workbookPath, logger);
                if (!loadResult.Success || loadResult.Input == null)
                {
                    SetExitStage("workbook_invalid");
                    var failureMessage = string.IsNullOrWhiteSpace(loadResult.ErrorMessage)
                        ? "Workbook could not be parsed."
                        : loadResult.ErrorMessage;
                    editor.WriteMessage("\nATSBUILD_XLS_BATCH workbook error: " + failureMessage);
                    EmitExit("error");
                    logger.Dispose();
                    return;
                }

                editor.WriteMessage("\nATSBUILD_XLS_BATCH workbook: " + loadResult.WorkbookPath);
                editor.WriteMessage(
                    $"\nATSBUILD_XLS_BATCH parsed {loadResult.SourceRowCount} worksheet row(s) into {loadResult.Input.SectionRequests.Count} build request(s) from sheet '{loadResult.WorksheetName}'.");

                ExecuteAtsBuildFromInput(
                    commandName: "ATSBUILD_XLS_BATCH",
                    inputSourceLabel: "XLS_BATCH",
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
                    emitExit: EmitExit,
                    onBuildCompleted: context =>
                    {
                        if (string.IsNullOrWhiteSpace(dxfPath))
                        {
                            return;
                        }

                        SetExitStage("export_dxf");
                        ExportBatchDxf(context.Database, dxfPath, context.Logger, context.Editor);
                    });
            }
            catch (System.Exception ex)
            {
                SetExitStage("startup_error");
                editor.WriteMessage("\nATSBUILD_XLS_BATCH startup error: " + ex.Message);
                logger.WriteLine("ATSBUILD_XLS_BATCH startup error: " + ex);
                EmitExit("error");
                logger.Dispose();
            }
        }

        private static string? ResolveBatchWorkbookPath(Editor editor)
        {
            var workbookPath = Environment.GetEnvironmentVariable(AtsBuildBatchWorkbookEnvVar);
            if (!string.IsNullOrWhiteSpace(workbookPath))
            {
                return workbookPath;
            }

            return PromptForAtsBuildWorkbookPath(editor);
        }

        private static string? ResolveBatchDxfPath()
        {
            var dxfPath = Environment.GetEnvironmentVariable(AtsBuildBatchDxfPathEnvVar);
            if (string.IsNullOrWhiteSpace(dxfPath))
            {
                return null;
            }

            return Path.GetFullPath(dxfPath.Trim().Trim('"'));
        }

        private static void ExportBatchDxf(Database database, string dxfPath, Logger logger, Editor editor)
        {
            if (database == null || string.IsNullOrWhiteSpace(dxfPath))
            {
                return;
            }

            var resolvedPath = Path.GetFullPath(dxfPath.Trim().Trim('"'));
            var folder = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            database.DxfOut(resolvedPath, 16, DwgVersion.Current);
            logger.WriteLine("ATSBUILD_XLS_BATCH dxf exported: " + resolvedPath);
            editor.WriteMessage("\nATSBUILD_XLS_BATCH dxf: " + resolvedPath);
        }
    }
}
