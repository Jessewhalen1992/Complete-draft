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
            logger.WriteLine("ATSBUILD_XLS " + DescribeFinalUsecOutputRelayerEnvironment());

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
            Action<string> emitExit,
            Action<AtsBuildSessionContext>? onBuildCompleted = null)
        {
            if (input == null)
            {
                setExitStage("input_null");
                editor.WriteMessage($"\n{commandName} cancelled.");
                emitExit("cancelled");
                logger.Dispose();
                return;
            }

            var context = new AtsBuildSessionContext(
                commandName,
                inputSourceLabel,
                editor,
                database,
                logger,
                dllFolder,
                config,
                companyLookup,
                purposeLookup,
                input,
                drawQuarterView,
                setExitStage,
                getExitStage,
                emitExit);
            LogAtsBuildSessionInput(context);

            try
            {
                if (!TryExecuteAtsBuildSession(context))
                {
                    return;
                }

                onBuildCompleted?.Invoke(context);
                context.SetExitStage("completed");
                context.EmitExit("ok");
            }
            catch (System.Exception ex)
            {
                context.SetExitStage("fatal_exception");
                var failureText = $"{context.CommandName} failed at stage '{context.GetExitStage()}': {ex.Message}";
                context.Logger.WriteLine(failureText);
                context.Logger.WriteLine(ex.ToString());
                try
                {
                    context.WriteMessage(failureText);
                }
                catch
                {
                    // Best-effort command line reporting only.
                }

                context.EmitExit("error");
            }
            finally
            {
                context.Logger.Dispose();
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

