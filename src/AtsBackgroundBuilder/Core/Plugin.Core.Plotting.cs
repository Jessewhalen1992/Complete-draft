using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private const string AtsBuildPlotDictionaryName = "ATSBUILD";
        private const string AtsBuildLastPlotWindowRecordName = "LAST_BUILD_WINDOW";
        private const double AtsPlotWindowPaddingMeters = 100.0;
        private const string AtsPlotDeviceName = "DWG To PDF.pc3";
        private const string AtsPlotStyleSheet = "Plot Color.ctb";
        private const string ModelLayoutName = "Model";

        [CommandMethod("ATSPLOT_AUTO")]
        public void AtsPlotAuto()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var database = doc.Database;

            if (!TryReadLastAtsBuildPlotWindow(database, out var windowExtents, out var savedAtText))
            {
                editor.WriteMessage("\nATSPLOT_AUTO: no ATSBUILD extents are saved in this drawing yet. Run ATSBUILD or ATSBUILD_XLS first.");
                return;
            }

            try
            {
                editor.WriteMessage($"\nATSPLOT_AUTO: plotting last ATSBUILD window saved at {savedAtText}.");
                var outputPath = QueueLastAtsBuildWindowNativePlot(doc, database, editor, windowExtents);
                editor.WriteMessage("\nATSPLOT_AUTO queued: " + outputPath);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nATSPLOT_AUTO failed: " + ex.Message);
            }
        }

        private static void PersistLastAtsBuildPlotWindow(
            Database database,
            SectionDrawResult sectionDrawResult,
            IReadOnlyCollection<ObjectId> importedDispositionPolylines,
            Logger? logger)
        {
            if (database == null || sectionDrawResult == null)
            {
                return;
            }

            if (!TryComputeAtsBuildPlotWindow(database, sectionDrawResult, importedDispositionPolylines, out var extents))
            {
                logger?.WriteLine("ATSPLOT_AUTO extents: skipped persistence because build extents could not be computed.");
                return;
            }

            try
            {
                using var tr = database.TransactionManager.StartTransaction();
                var root = (DBDictionary)tr.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
                DBDictionary atsDict;
                if (root.Contains(AtsBuildPlotDictionaryName))
                {
                    atsDict = (DBDictionary)tr.GetObject(root.GetAt(AtsBuildPlotDictionaryName), OpenMode.ForWrite);
                }
                else
                {
                    root.UpgradeOpen();
                    atsDict = new DBDictionary();
                    root.SetAt(AtsBuildPlotDictionaryName, atsDict);
                    tr.AddNewlyCreatedDBObject(atsDict, true);
                }

                Xrecord record;
                if (atsDict.Contains(AtsBuildLastPlotWindowRecordName))
                {
                    record = (Xrecord)tr.GetObject(atsDict.GetAt(AtsBuildLastPlotWindowRecordName), OpenMode.ForWrite);
                }
                else
                {
                    record = new Xrecord();
                    atsDict.SetAt(AtsBuildLastPlotWindowRecordName, record);
                    tr.AddNewlyCreatedDBObject(record, true);
                }

                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                    new TypedValue((int)DxfCode.Real, extents.MinPoint.X),
                    new TypedValue((int)DxfCode.Real, extents.MinPoint.Y),
                    new TypedValue((int)DxfCode.Real, extents.MaxPoint.X),
                    new TypedValue((int)DxfCode.Real, extents.MaxPoint.Y));

                tr.Commit();
                logger?.WriteLine(
                    $"ATSPLOT_AUTO extents saved: min=({extents.MinPoint.X:F3},{extents.MinPoint.Y:F3}) max=({extents.MaxPoint.X:F3},{extents.MaxPoint.Y:F3}).");
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("ATSPLOT_AUTO extents persistence failed: " + ex.Message);
            }
        }

        private static bool TryReadLastAtsBuildPlotWindow(Database database, out Extents3d extents, out string savedAtText)
        {
            extents = default;
            savedAtText = string.Empty;
            if (database == null)
            {
                return false;
            }

            using var tr = database.TransactionManager.StartTransaction();
            var root = (DBDictionary)tr.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!root.Contains(AtsBuildPlotDictionaryName))
            {
                return false;
            }

            var atsDict = (DBDictionary)tr.GetObject(root.GetAt(AtsBuildPlotDictionaryName), OpenMode.ForRead);
            if (!atsDict.Contains(AtsBuildLastPlotWindowRecordName))
            {
                return false;
            }

            var record = (Xrecord)tr.GetObject(atsDict.GetAt(AtsBuildLastPlotWindowRecordName), OpenMode.ForRead);
            var data = record.Data?.AsArray();
            if (data == null || data.Length < 5)
            {
                return false;
            }

            savedAtText = Convert.ToString(data[0].Value) ?? string.Empty;
            var minX = Convert.ToDouble(data[1].Value);
            var minY = Convert.ToDouble(data[2].Value);
            var maxX = Convert.ToDouble(data[3].Value);
            var maxY = Convert.ToDouble(data[4].Value);

            extents = new Extents3d(
                new Point3d(minX, minY, 0.0),
                new Point3d(maxX, maxY, 0.0));
            return true;
        }

        private static bool TryComputeAtsBuildPlotWindow(
            Database database,
            SectionDrawResult sectionDrawResult,
            IReadOnlyCollection<ObjectId> importedDispositionPolylines,
            out Extents3d extents)
        {
            extents = default;
            if (database == null || sectionDrawResult == null)
            {
                return false;
            }

            var candidateIds = new List<ObjectId>();
            candidateIds.AddRange(sectionDrawResult.SectionPolylineIds ?? new List<ObjectId>());
            candidateIds.AddRange(sectionDrawResult.ContextSectionPolylineIds ?? new List<ObjectId>());
            candidateIds.AddRange(sectionDrawResult.LabelQuarterPolylineIds ?? new List<ObjectId>());
            if (importedDispositionPolylines != null)
            {
                candidateIds.AddRange(importedDispositionPolylines);
            }

            var hasAnyExtents = false;
            using var tr = database.TransactionManager.StartTransaction();
            foreach (var id in candidateIds.Where(id => !id.IsNull && id.IsValid).Distinct())
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity))
                {
                    continue;
                }

                Extents3d entityExtents;
                try
                {
                    entityExtents = entity.GeometricExtents;
                }
                catch
                {
                    continue;
                }

                if (!hasAnyExtents)
                {
                    extents = entityExtents;
                    hasAnyExtents = true;
                }
                else
                {
                    extents.AddPoint(entityExtents.MinPoint);
                    extents.AddPoint(entityExtents.MaxPoint);
                }
            }

            if (!hasAnyExtents)
            {
                return false;
            }

            extents = ExpandExtents(extents, AtsPlotWindowPaddingMeters);
            return true;
        }

        private static Extents3d ExpandExtents(Extents3d extents, double padding)
        {
            return new Extents3d(
                new Point3d(extents.MinPoint.X - padding, extents.MinPoint.Y - padding, extents.MinPoint.Z),
                new Point3d(extents.MaxPoint.X + padding, extents.MaxPoint.Y + padding, extents.MaxPoint.Z));
        }

        private static string QueueLastAtsBuildWindowNativePlot(
            Document doc,
            Database database,
            Editor editor,
            Extents3d windowExtents)
        {
            using var tr = database.TransactionManager.StartTransaction();

            var layoutManager = LayoutManager.Current;
            var modelLayoutId = layoutManager.GetLayoutId(ModelLayoutName);
            var modelLayout = (Layout)tr.GetObject(modelLayoutId, OpenMode.ForRead);

            var outputPath = BuildAutoPlotOutputPath(doc);
            var fitsAtMapScale5000 = WarnIfPlotMayNotFit(editor, modelLayout.PlotPaperSize, windowExtents);
            var shouldUseFitScale = !fitsAtMapScale5000;
            if (shouldUseFitScale)
            {
                editor.WriteMessage(
                    "\nATSPLOT_AUTO: map scale 1:5000 exceeds current paper; using Fit scale so the full ATSBUILD window plots.");
            }
            var plotConfigurationName = modelLayout.PlotConfigurationName ?? string.Empty;
            var currentStyleSheet = modelLayout.CurrentStyleSheet ?? string.Empty;
            var canonicalMediaName = modelLayout.CanonicalMediaName ?? string.Empty;
            var plotPaperUnits = modelLayout.PlotPaperUnits;
            var usesCurrentPdfDevice = ShouldReuseCurrentPdfDevice(plotConfigurationName);
            var effectiveDevice = usesCurrentPdfDevice
                ? plotConfigurationName
                : AtsPlotDeviceName;

            var activeDevice = string.IsNullOrWhiteSpace(plotConfigurationName)
                ? "current"
                : plotConfigurationName;
            var activeStyle = string.IsNullOrWhiteSpace(currentStyleSheet)
                ? "current"
                : currentStyleSheet;
            var activeMedia = string.IsNullOrWhiteSpace(canonicalMediaName)
                ? "(current media not reported)"
                : canonicalMediaName;

            editor.WriteMessage($"\nATSPLOT_AUTO: using native plot flow from model config '{activeDevice}', media '{activeMedia}', style '{activeStyle}'.");
            if (!usesCurrentPdfDevice && !string.IsNullOrWhiteSpace(plotConfigurationName))
            {
                editor.WriteMessage(
                    $"\nATSPLOT_AUTO: overriding unsupported model device '{plotConfigurationName}' with '{effectiveDevice}'.");
            }

            tr.Commit();

            var originalFileDia = GetIntegerSystemVariable("FILEDIA", 1);
            var originalBackgroundPlot = GetIntegerSystemVariable("BACKGROUNDPLOT", 0);

            using (doc.LockDocument())
            {
                if (!string.Equals(layoutManager.CurrentLayout, ModelLayoutName, StringComparison.OrdinalIgnoreCase))
                {
                    layoutManager.CurrentLayout = ModelLayoutName;
                }

                var lispScript = BuildNativePlotLispScript(
                    plotConfigurationName,
                    currentStyleSheet,
                    plotPaperUnits,
                    shouldUseFitScale,
                    windowExtents,
                    outputPath,
                    originalFileDia,
                    originalBackgroundPlot);

                doc.SendStringToExecute(lispScript, true, false, false);
            }

            return outputPath;
        }

        private static string BuildNativePlotLispScript(
            string plotConfigurationName,
            string currentStyleSheet,
            PlotPaperUnit plotPaperUnits,
            bool useFitScale,
            Extents3d windowExtents,
            string outputPath,
            int originalFileDia,
            int originalBackgroundPlot)
        {
            var deviceResponse = ResolvePlotDeviceResponse(plotConfigurationName);

            var styleResponse = currentStyleSheet.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : AtsPlotStyleSheet;

            var scaleResponse = useFitScale
                ? "Fit"
                : BuildMapScale5000Prompt(plotPaperUnits);
            var minPoint = new Point2d(windowExtents.MinPoint.X, windowExtents.MinPoint.Y);
            var maxPoint = new Point2d(windowExtents.MaxPoint.X, windowExtents.MaxPoint.Y);

            var sb = new StringBuilder();
            AppendLispForm(sb, "(vl-load-com)");
            AppendLispForm(sb, "(setvar \"FILEDIA\" 0)");
            AppendLispForm(sb, "(setvar \"BACKGROUNDPLOT\" 0)");
            AppendLispForm(
                sb,
                string.Join(
                    " ",
                    new[]
                    {
                        "(vl-cmdf",
                        QuoteForLisp("_.-PLOT"),
                        QuoteForLisp("_Yes"),
                        QuoteForLisp(ModelLayoutName),
                        QuoteForLisp(deviceResponse),
                        QuoteForLisp(string.Empty),
                        QuoteForLisp(string.Empty),
                        QuoteForLisp(string.Empty),
                        QuoteForLisp("_No"),
                        QuoteForLisp("_Window"),
                        QuoteForLisp(FormatPointForCommand(minPoint)),
                        QuoteForLisp(FormatPointForCommand(maxPoint)),
                        QuoteForLisp(scaleResponse),
                        QuoteForLisp("_Center"),
                        QuoteForLisp("_Yes"),
                        QuoteForLisp(styleResponse),
                        QuoteForLisp("_Yes"),
                        QuoteForLisp("As displayed"),
                        QuoteForLisp(NormalizePathForLisp(outputPath)),
                        QuoteForLisp("_No"),
                        QuoteForLisp("_Yes"),
                        ")"
                    }));
            AppendLispForm(sb, $"(setvar \"BACKGROUNDPLOT\" {originalBackgroundPlot.ToString(CultureInfo.InvariantCulture)})");
            AppendLispForm(sb, $"(setvar \"FILEDIA\" {originalFileDia.ToString(CultureInfo.InvariantCulture)})");
            AppendLispForm(sb, "(princ)");
            return sb.ToString();
        }

        private static void AppendLispForm(StringBuilder sb, string form)
        {
            sb.Append(form);
            sb.Append('\n');
        }

        private static string QuoteForLisp(string value)
        {
            var safe = (value ?? string.Empty).Replace("\\", "/").Replace("\"", "\\\"");
            return "\"" + safe + "\"";
        }

        private static string NormalizePathForLisp(string path)
        {
            return (path ?? string.Empty).Replace("\\", "/");
        }

        private static string BuildMapScale5000Prompt(PlotPaperUnit paperUnits)
        {
            return paperUnits == PlotPaperUnit.Inches
                ? "1=127"
                : "1=5";
        }

        private static string ResolvePlotDeviceResponse(string plotConfigurationName)
        {
            return ShouldReuseCurrentPdfDevice(plotConfigurationName)
                ? string.Empty
                : AtsPlotDeviceName;
        }

        private static bool ShouldReuseCurrentPdfDevice(string plotConfigurationName)
        {
            if (string.IsNullOrWhiteSpace(plotConfigurationName))
            {
                return false;
            }

            if (plotConfigurationName.IndexOf("Adobe PDF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return plotConfigurationName.IndexOf("DWG To PDF", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetIntegerSystemVariable(string name, int fallback)
        {
            try
            {
                return Convert.ToInt32(Application.GetSystemVariable(name), CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static string FormatPointForCommand(Point2d point)
        {
            return FormatDoubleForCommand(point.X) + "," + FormatDoubleForCommand(point.Y);
        }

        private static string FormatDoubleForCommand(double value)
        {
            return value.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        private static bool WarnIfPlotMayNotFit(Editor editor, Point2d paperSize, Extents3d windowExtents)
        {
            var windowWidthMeters = Math.Abs(windowExtents.MaxPoint.X - windowExtents.MinPoint.X);
            var windowHeightMeters = Math.Abs(windowExtents.MaxPoint.Y - windowExtents.MinPoint.Y);

            // ATSBUILD geometry is in meters. At 1:5000, 1 mm on paper represents 5 m on the drawing.
            var requiredWidthMm = windowWidthMeters / 5.0;
            var requiredHeightMm = windowHeightMeters / 5.0;

            var fitsWithoutRotation =
                requiredWidthMm <= paperSize.X &&
                requiredHeightMm <= paperSize.Y;
            var fitsWithRotation =
                requiredWidthMm <= paperSize.Y &&
                requiredHeightMm <= paperSize.X;
            var fitsAtMapScale5000 = fitsWithoutRotation || fitsWithRotation;

            if (!fitsAtMapScale5000)
            {
                editor.WriteMessage(
                    $"\nATSPLOT_AUTO warning: saved build window is about {requiredWidthMm:F1}mm x {requiredHeightMm:F1}mm at 1:5000, which may exceed the selected paper size {paperSize.X:F1}mm x {paperSize.Y:F1}mm.");
            }

            return fitsAtMapScale5000;
        }

        private static string BuildAutoPlotOutputPath(Document doc)
        {
            var drawingPath = doc.Name ?? string.Empty;
            var drawingDirectory = Path.GetDirectoryName(drawingPath);
            if (string.IsNullOrWhiteSpace(drawingDirectory) || !Directory.Exists(drawingDirectory))
            {
                drawingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var drawingName = Path.GetFileNameWithoutExtension(drawingPath);
            if (string.IsNullOrWhiteSpace(drawingName))
            {
                drawingName = "Drawing";
            }

            var basePath = Path.Combine(drawingDirectory, drawingName + "_ATSPLOT_AUTO.pdf");
            if (!File.Exists(basePath))
            {
                return basePath;
            }

            return Path.Combine(
                drawingDirectory,
                drawingName + "_ATSPLOT_AUTO_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf");
        }
    }
}
