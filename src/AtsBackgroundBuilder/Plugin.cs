/////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ImportExport;

namespace AtsBackgroundBuilder
{
    public partial class Plugin : IExtensionApplication
    {
        private const string RaDiagBuildTag = "RA-DIAG-BUILD 2026-02-08-alt6";
        private const string LayerUsecBase = "L-USEC";
        private const string LayerUsecZero = "L-USEC-0";
        private const string LayerUsecTwenty = "L-USEC2012";
        private const string LayerUsecThirty = "L-USEC3018";
        private const double RoadAllowanceUsecWidthMeters = SectionRules.RoadAllowanceUsecWidthMeters;
        private const double RoadAllowanceSecWidthMeters = SectionRules.RoadAllowanceSecWidthMeters;
        private const double SurveyedUnsurveyedThresholdMeters = SectionRules.SurveyedUnsurveyedThresholdMeters;
        private const double CorrectionLineInsetMeters = SectionRules.CorrectionLineInsetMeters;
        private const double CorrectionLinePairGapMeters = SectionRules.CorrectionLinePairGapMeters;
        private const double RoadAllowanceWidthToleranceMeters = SectionRules.RoadAllowanceWidthToleranceMeters;
        private const double RoadAllowanceGapOffsetToleranceMeters = SectionRules.RoadAllowanceGapOffsetToleranceMeters;
        private const double MinAdjustableLsdLineLengthMeters = SectionRules.MinAdjustableLsdLineLengthMeters;
        // Heavy road-allowance tracing is off by default; enable with ATSBUILD_RA_DIAG=1 when needed.
        private static readonly bool EnableRoadAllowanceDiagnostics =
            string.Equals(Environment.GetEnvironmentVariable("ATSBUILD_RA_DIAG"), "1", StringComparison.OrdinalIgnoreCase);
        // 100m DEFPOINTS buffer windows are off by default for performance testing.
        private static readonly bool EnableBufferedQuarterWindowDrawing =
            string.Equals(Environment.GetEnvironmentVariable("ATSBUILD_DRAW_100M_BUFFER"), "1", StringComparison.OrdinalIgnoreCase);
        // Export final CAD diagnostic layers (L-SEC/L-USEC/L-QSEC/L-SECTION-LSD) as GeoJSON for py viewer parity.
        private static readonly bool EnableCadGeoJsonExport =
            string.Equals(Environment.GetEnvironmentVariable("ATSBUILD_EXPORT_GEOJSON"), "1", StringComparison.OrdinalIgnoreCase);
        private static readonly string CadGeoJsonExportPath =
            Environment.GetEnvironmentVariable("ATSBUILD_EXPORT_GEOJSON_PATH") ?? string.Empty;
        private static readonly object SectionOutlineCacheLock = new object();
        private static readonly object BufferedDefpointsWindowLock = new object();
        private static readonly Dictionary<string, SectionOutline?> SectionOutlineCache = new Dictionary<string, SectionOutline?>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> FolderIndexCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<ObjectId> BufferedDefpointsWindowIds = new HashSet<ObjectId>();

        public void Initialize()
        {
        }

        public void Terminate()
        {
        }

        [CommandMethod("ATSBUILD")]
        public void AtsBuild()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
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
            logger.WriteLine($"ATSBUILD assembly: {assemblyPath} (local={assemblyLocalStamp:yyyy-MM-dd h:mm:ss tt}, utc={assemblyUtcStamp:yyyy-MM-dd HH:mm:ss})");
            var exitStage = "startup";
            void EmitExit(string reason)
            {
                var msg = $"ATSBUILD exit stage: {exitStage} ({reason})";
                editor.WriteMessage("\n" + msg);
                logger.WriteLine(msg);
            }

            var configPath = Path.Combine(dllFolder, "Config.json");
            var config = Config.Load(configPath, logger);
            config.AllowMultiQuarterDispositions = true;

            var companyLookupPath = ResolveLookupPath(config.LookupFolder, config.CompanyLookupFile, dllFolder, "CompanyLookup.xlsx");
            var purposeLookupPath = ResolveLookupPath(config.LookupFolder, config.PurposeLookupFile, dllFolder, "PurposeLookup.xlsx");

            var companyLookup = ExcelLookup.Load(companyLookupPath, logger);
            var purposeLookup = ExcelLookup.Load(purposeLookupPath, logger);

            // ------------------------------------------------------------------
            // UI input (replaces command line prompts)
            // ------------------------------------------------------------------
            AtsBuildInput? input = null;
            try
            {
                var clientNames = companyLookup.ValuesByExtra("C");
                if (clientNames.Count == 0)
                {
                    logger.WriteLine("No company rows flagged with Extra='C'; using full company list for client dropdown.");
                    clientNames = companyLookup.Values;
                }

                using (var form = new AtsBuildForm(clientNames, config))
                {
                    var dr = form.ShowDialog();
                    if (dr != System.Windows.Forms.DialogResult.OK || form.Result == null)
                    {
                        exitStage = "ui_cancelled";
                        editor.WriteMessage("\nATSBUILD cancelled.");
                        EmitExit("cancelled");
                        logger.Dispose();
                        return;
                    }

                    input = form.Result;
                }
            }
            catch (System.Exception ex)
            {
                exitStage = "ui_error";
                editor.WriteMessage($"\nATSBUILD UI error: {ex.Message}");
                logger.WriteLine("ATSBUILD UI error: " + ex);
                EmitExit("error");
                logger.Dispose();
                return;
            }

            if (input == null)
            {
                exitStage = "input_null";
                editor.WriteMessage("\nATSBUILD cancelled.");
                EmitExit("cancelled");
                logger.Dispose();
                return;
            }

            // Enforce existing behavior (multi-quarter labeling) unless you later decide to expose this in the UI.
            config.AllowMultiQuarterDispositions = true;
            config.TextHeight = input.TextHeight;
            config.MaxOverlapAttempts = input.MaxOverlapAttempts;

            if (!config.UseSectionIndex)
            {
                exitStage = "config_use_section_index_false";
                editor.WriteMessage("\nConfig.UseSectionIndex=false. This build requires the section index workflow.");
                EmitExit("error");
                logger.Dispose();
                return;
            }

            // Build the section geometry used to determine 1/4s.
            // NOTE: These entities are considered "temporary" unless ATS fabric is enabled (see cleanup at the end).
            var sectionDrawResult = DrawSectionsFromRequests(
                editor,
                database,
                input.SectionRequests,
                config,
                logger,
                input.DrawLsdSubdivisionLines);
            exitStage = "sections_built";
            var quarterPolylinesForLabelling = sectionDrawResult.LabelQuarterPolylineIds;
            if (quarterPolylinesForLabelling.Count == 0)
            {
                exitStage = "no_quarters_generated";
                editor.WriteMessage("\nNo quarter polylines generated from the section index (check your grid inputs)." );

                // Ensure we don't leave temporary section outlines behind when ATS fabric is unchecked.
                CleanupAfterBuild(database, sectionDrawResult, new List<ObjectId>(), input, logger);

                EmitExit("error");
                logger.Dispose();
                return;
            }

            if (input.IncludeP3Shapefiles)
            {
                exitStage = "p3_import";
                var p3Summary = ImportP3Shapefiles(
                    database,
                    editor,
                    logger,
                    sectionDrawResult.SectionPolylineIds);
                editor.WriteMessage($"\nP3 import: imported {p3Summary.ImportedEntities}, filtered {p3Summary.FilteredEntities}, failures {p3Summary.ImportFailures}.");
                logger.WriteLine($"P3 import summary: imported={p3Summary.ImportedEntities}, filtered={p3Summary.FilteredEntities}, failures={p3Summary.ImportFailures}");
            }

            var dispositionPolylines = new List<ObjectId>();
            ShapefileImportSummary importSummary;
            if (input.IncludeDispositionLinework || input.IncludeDispositionLabels)
            {
                exitStage = "disposition_import";
                importSummary = ShapefileImporter.ImportShapefiles(
                    database,
                    editor,
                    logger,
                    config,
                    sectionDrawResult.SectionPolylineIds,
                    dispositionPolylines);
                if (dispositionPolylines.Count == 0)
                {
                    editor.WriteMessage("\nNo disposition polylines imported from shapefiles.");
                    // Continue so ATS fabric / cleanup behavior still runs.
                }
            }
            else
            {
                importSummary = new ShapefileImportSummary();
            }

            var currentClient = input.CurrentClient;
            if (string.IsNullOrWhiteSpace(currentClient))
            {
                exitStage = "missing_current_client";
                editor.WriteMessage("\nCurrent client is required.");
                EmitExit("error");
                logger.Dispose();
                return;
            }

            var layerManager = new LayerManager(database);
            var dispositions = new List<DispositionInfo>();
            var result = new SummaryResult();
            var shouldLoadSupplementalSectionInfos = input.IncludeDispositionLabels && dispositionPolylines.Count > 0;
            var supplementalSectionInfos = shouldLoadSupplementalSectionInfos
                ? LoadSupplementalSectionSpatialInfos(input.SectionRequests, config, logger)
                : new List<SectionSpatialInfo>();

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                exitStage = "processing_dispositions";
                var lsdCells = new List<LsdCellInfo>();
                var sectionInfos = new List<SectionSpatialInfo>();
                if (shouldLoadSupplementalSectionInfos)
                {
                    lsdCells = BuildSectionLsdCells(transaction, sectionDrawResult.SectionNumberByPolylineId);
                    sectionInfos = BuildSectionSpatialInfos(transaction, sectionDrawResult.SectionNumberByPolylineId);
                }
                try
                {
                    foreach (var info in supplementalSectionInfos)
                    {
                        sectionInfos.Add(info);
                        BuildLsdCellsForSection(info.SectionPolyline, info.Section, lsdCells);
                    }

                    foreach (var id in dispositionPolylines)
                    {
                        var ent = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || !GeometryUtils.TryGetClosedBoundaryClone(ent, out var clone))
                        {
                            result.SkippedNotClosed++;
                            continue;
                        }

                        // Work with an in-memory clone after the transaction ends.
                        // NOTE: clone is a DBObject not in the database; keep it alive for label placement.

                        result.TotalDispositions++;
                        var od = OdHelpers.ReadObjectData(id, logger);
                        if (od == null)
                        {
                            result.SkippedNoOd++;
                            continue;
                        }

                    var dispNum = od.TryGetValue("DISP_NUM", out var dispRaw) ? dispRaw : string.Empty;
                    var company = od.TryGetValue("COMPANY", out var companyRaw) ? companyRaw : string.Empty;
                    var purpose = od.TryGetValue("PURPCD", out var purposeRaw) ? purposeRaw : string.Empty;
                    var odDimension = od.TryGetValue("DIMENSION", out var dimensionRaw) ? dimensionRaw : string.Empty;

                        var mappedCompany = MapValue(companyLookup, company, company);
                        var mappedPurpose = MapValue(purposeLookup, purpose, purpose);
                        var purposeExtra = purposeLookup.Lookup(purpose)?.Extra ?? string.Empty;

                    var dispNumFormatted = FormatDispNum(dispNum);
                    var safePoint = GeometryUtils.GetSafeInteriorPoint(clone);

                    var isSurfaceLabel = (mappedPurpose ?? string.Empty).IndexOf("(Surface)", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (input.IncludeDispositionLabels && (IsWellSitePurpose(purpose) || isSurfaceLabel))
                    {
                        var normalizedPurpose = NormalizePurposeCode(purpose);
                        editor.WriteMessage($"\nWELLSITE DEBUG: DISP={dispNumFormatted} PURPCD='{purpose}' normalized='{normalizedPurpose}' mapped='{mappedPurpose}' surface={isSurfaceLabel}");
                        logger.WriteLine($"WELLSITE DEBUG: DISP={dispNumFormatted} PURPCD='{purpose}' normalized='{normalizedPurpose}' mapped='{mappedPurpose}' surface={isSurfaceLabel}");

                        var lsdSecToken = GetDominantLsdSectionToken(clone, lsdCells);
                        editor.WriteMessage($"\nWELLSITE DEBUG: primary-token='{lsdSecToken}' lsd-cells={lsdCells.Count}");
                        logger.WriteLine($"WELLSITE DEBUG: primary-token='{lsdSecToken}' lsd-cells={lsdCells.Count}");
                        if (string.IsNullOrWhiteSpace(lsdSecToken))
                        {
                            lsdSecToken = GetDominantLsdSectionTokenBySection(clone, sectionInfos);
                            editor.WriteMessage($"\nWELLSITE DEBUG: fallback-token='{lsdSecToken}' section-infos={sectionInfos.Count}");
                            logger.WriteLine($"WELLSITE DEBUG: fallback-token='{lsdSecToken}' section-infos={sectionInfos.Count}");
                            if (string.IsNullOrWhiteSpace(lsdSecToken))
                            {
                                lsdSecToken = GetPointBasedLsdSectionToken(clone, sectionInfos, out var pointDebug);
                                editor.WriteMessage($"\nWELLSITE DEBUG: point-token='{lsdSecToken}' detail={pointDebug}");
                                logger.WriteLine($"WELLSITE DEBUG: point-token='{lsdSecToken}' detail={pointDebug}");
                            }
                            if (string.IsNullOrWhiteSpace(lsdSecToken))
                            {
                                var overlapDebug = GetSectionOverlapDebugString(clone, sectionInfos);
                                editor.WriteMessage($"\nWELLSITE DEBUG: overlaps {overlapDebug}");
                                logger.WriteLine($"WELLSITE DEBUG: overlaps {overlapDebug}");
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(lsdSecToken))
                        {
                            mappedPurpose = lsdSecToken + " " + mappedPurpose;
                            editor.WriteMessage($"\nWELLSITE DEBUG: final surface line='{mappedPurpose}'");
                            logger.WriteLine($"WELLSITE DEBUG: final surface line='{mappedPurpose}'");
                        }
                    }
                    else if (input.IncludeDispositionLabels && (mappedPurpose ?? string.Empty).IndexOf("(Surface)", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var normalizedPurpose = NormalizePurposeCode(purpose);
                        editor.WriteMessage($"\nWELLSITE DEBUG: skipped (not detected as wellsite) DISP={dispNumFormatted} PURPCD='{purpose}' normalized='{normalizedPurpose}' mapped='{mappedPurpose}'");
                        logger.WriteLine($"WELLSITE DEBUG: skipped (not detected as wellsite) DISP={dispNumFormatted} PURPCD='{purpose}' normalized='{normalizedPurpose}' mapped='{mappedPurpose}'");
                    }

                        // Default output layers; will be overridden when mapping succeeds.
                        string lineLayer = ent.Layer;
                        string textLayer = ent.Layer;

                    // Determine client/foreign layer names based on company and purpose.
                    // Purpose extra column supplies the layer suffix (e.g. ROW, PIPE, etc.).
                    string? lineLayerName = null;
                    string? textLayerName = null;
                    try
                    {
                        var purposeEntry = purposeLookup.Lookup(purpose);
                        var suffix = purposeEntry?.Extra?.Trim() ?? string.Empty;
                        if (suffix.StartsWith("-", StringComparison.InvariantCulture))
                            suffix = suffix.Substring(1);
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            // OD COMPANY is often a code (lookup key), while the UI client list comes from
                            // the lookup VALUE column. Compare against the mapped company first.
                            bool isClient = string.Equals(currentClient, mappedCompany, StringComparison.InvariantCultureIgnoreCase)
                                            || string.Equals(currentClient, company, StringComparison.InvariantCultureIgnoreCase);

                            var prefix = isClient ? "C" : "F";
                            lineLayerName = $"{prefix}-{suffix}";
                            textLayerName = $"{lineLayerName}-T";
                        }
                    }
                    catch
                    {
                        // ignore lookup failures; fall back to original layer
                    }

                    if (!string.IsNullOrEmpty(lineLayerName))
                        lineLayer = lineLayerName;
                    if (!string.IsNullOrEmpty(textLayerName))
                        textLayer = textLayerName;

                    // Ensure the layer exists before assigning
                    layerManager.EnsureLayer(lineLayer);
                    var lineEntity = transaction.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (lineEntity != null && lineEntity.Layer != lineLayer)
                    {
                        lineEntity.Layer = lineLayer;
                        lineEntity.ColorIndex = 256;  // ensure ByLayer colour
                    }

                    if (!input.IncludeDispositionLabels)
                    {
                        clone.Dispose();
                        continue;
                    }

                    var requiresWidth = PurposeRequiresWidth(purpose, config);
                    string labelText;

                    if (requiresWidth)
                    {
                        var info = new DispositionInfo(id, clone, string.Empty, lineLayer, textLayer, safePoint)
                        {
                            AllowLabelOutsideDisposition = config.AllowOutsideDispositionForWidthPurposes,
                            AddLeader = true,
                            RequiresWidth = true,
                            MappedCompany = mappedCompany ?? string.Empty,
                            MappedPurpose = mappedPurpose ?? string.Empty,
                            PurposeTitleCase = ToTitleCaseWords(purpose),
                            DispNumFormatted = dispNumFormatted,
                            OdDimension = odDimension ?? string.Empty
                        };
                        dispositions.Add(info);
                        continue;
                    }

                    labelText = mappedCompany + "\\P" + mappedPurpose + "\\P" + dispNumFormatted;

                    var nonWidthInfo = new DispositionInfo(id, clone, labelText, lineLayer, textLayer, safePoint)
                    {
                        AllowLabelOutsideDisposition = false,
                        AddLeader = false
                    };

                        dispositions.Add(nonWidthInfo);
                    }
                }
                finally
                {
                    DisposeLsdCells(lsdCells);
                    DisposeSectionInfos(sectionInfos);
                }
                transaction.Commit();
            }

            var quarters = new List<QuarterInfo>();
            if (input.IncludeDispositionLabels)
            {
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    if (sectionDrawResult.LabelQuarterInfos != null && sectionDrawResult.LabelQuarterInfos.Count > 0)
                    {
                        foreach (var info in sectionDrawResult.LabelQuarterInfos)
                        {
                            var polyline = transaction.GetObject(info.QuarterId, OpenMode.ForRead) as Polyline;
                            if (polyline == null || !polyline.Closed)
                            {
                                continue;
                            }

                            quarters.Add(new QuarterInfo((Polyline)polyline.Clone(), info.SectionKey, info.Quarter));
                        }
                    }
                    else
                    {
                        foreach (var id in quarterPolylinesForLabelling.Distinct())
                        {
                            var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                            if (polyline == null || !polyline.Closed)
                            {
                                continue;
                            }

                            quarters.Add(new QuarterInfo((Polyline)polyline.Clone()));
                        }
                    }
                    transaction.Commit();
                }
            }

            if (input.IncludeDispositionLabels)
            {
                exitStage = "placing_labels";
                var placer = new LabelPlacer(database, editor, layerManager, config, logger, input.UseAlignedDimensions);
                var placement = placer.PlaceLabels(quarters, dispositions, currentClient);

                result.LabelsPlaced = placement.LabelsPlaced;
                result.SkippedNoLayerMapping += placement.SkippedNoLayerMapping;
                result.OverlapForced = placement.OverlapForced;
                result.MultiQuarterProcessed = placement.MultiQuarterProcessed;
            }
            result.ImportedDispositions = importSummary.ImportedDispositions;
            result.DedupedDispositions = importSummary.DedupedDispositions;
            result.FilteredDispositions = importSummary.FilteredDispositions;
            result.ImportFailures = importSummary.ImportFailures;

            if (input.CheckPlsr)
            {
                exitStage = "plsr_check";
                if (!input.IncludeDispositionLabels)
                {
                    editor.WriteMessage("\nPLSR check skipped: Disposition labels are disabled.");
                    logger.WriteLine("PLSR check skipped: Disposition labels are disabled.");
                }
                else
                {
                    RunPlsrCheck(database, editor, logger, companyLookup, input, quarters);
                }
            }

            if (input.IncludeQuarterSectionLabels)
            {
                exitStage = "quarter_section_labels_final";
                PlaceQuarterSectionLabels(database, sectionDrawResult.LabelQuarterInfos ?? new List<QuarterLabelInfo>(), input.DrawLsdSubdivisionLines, logger);
            }

            // ------------------------------------------------------------------
            // Cleanup / output control
            // ------------------------------------------------------------------
            // Per your UI rules:
            //  - Quarter boxes used to determine 1/4s are ALWAYS removed.
            //  - If ATS fabric is unchecked, section outlines/labels created for calculation are removed.
            //  - If Dispositions (linework) is unchecked, imported disposition polylines are removed (labels remain).
            exitStage = "cleanup";
            CleanupAfterBuild(database, sectionDrawResult, dispositionPolylines, input, logger);
            if (EnableCadGeoJsonExport)
            {
                TryExportCadDiagnosticGeoJson(database, input.SectionRequests, dllFolder, logger);
            }

            exitStage = "summary";
            editor.WriteMessage("\nATSBUILD complete.");
            editor.WriteMessage("\nTotal dispositions: " + result.TotalDispositions);
            editor.WriteMessage("\nLabels placed: " + result.LabelsPlaced);
            editor.WriteMessage("\nSkipped (no OD): " + result.SkippedNoOd);
            editor.WriteMessage("\nSkipped (not closed): " + result.SkippedNotClosed);
            editor.WriteMessage("\nNo layer mapping: " + result.SkippedNoLayerMapping);
            editor.WriteMessage("\nOverlap forced: " + result.OverlapForced);
            editor.WriteMessage("\nMulti-quarter processed: " + result.MultiQuarterProcessed);
            editor.WriteMessage("\nImported dispositions: " + result.ImportedDispositions);
            editor.WriteMessage("\nFiltered out: " + result.FilteredDispositions);
            editor.WriteMessage("\nDeduped: " + result.DedupedDispositions);
            editor.WriteMessage("\nImport failures: " + result.ImportFailures);

            logger.WriteLine("ATSBUILD summary");
            logger.WriteLine("Total dispositions: " + result.TotalDispositions);
            logger.WriteLine("Labels placed: " + result.LabelsPlaced);
            logger.WriteLine("Skipped (no OD): " + result.SkippedNoOd);
            logger.WriteLine("Skipped (not closed): " + result.SkippedNotClosed);
            logger.WriteLine("No layer mapping: " + result.SkippedNoLayerMapping);
            logger.WriteLine("Overlap forced: " + result.OverlapForced);
            logger.WriteLine("Multi-quarter processed: " + result.MultiQuarterProcessed);
            logger.WriteLine("Imported dispositions: " + result.ImportedDispositions);
            logger.WriteLine("Filtered out: " + result.FilteredDispositions);
            logger.WriteLine("Deduped: " + result.DedupedDispositions);
            logger.WriteLine("Import failures: " + result.ImportFailures);
            exitStage = "completed";
            EmitExit("ok");
            logger.Dispose();
        }

        private static void CleanupAfterBuild(
            Database database,
            SectionDrawResult sectionDrawResult,
            List<ObjectId> dispositionPolylineIds,
            AtsBuildInput input,
            Logger logger)
        {
            if (database == null || sectionDrawResult == null || input == null)
                return;

            try
            {
                if (!input.IncludeAtsFabric)
                {
                    // If ATS fabric is not requested, remove all temporary section geometry.
                    EraseEntities(database, sectionDrawResult.QuarterPolylineIds, logger, "quarter boxes");
                    EraseEntities(database, sectionDrawResult.QuarterHelperEntityIds, logger, "quarter helper lines");
                    EraseEntities(database, sectionDrawResult.SectionPolylineIds, logger, "section outlines");
                    EraseEntities(database, sectionDrawResult.ContextSectionPolylineIds, logger, "context section pieces");
                    EraseEntities(database, sectionDrawResult.SectionLabelEntityIds, logger, "section labels");
                }
                else
                {
                    // Keep mapped section lines and generated linework; remove temp quarter polygons only.
                    EraseEntities(database, sectionDrawResult.QuarterPolylineIds, logger, "quarter boxes");
                }

                // If disposition linework is NOT requested, erase imported disposition polylines after labels are placed.
                if (!input.IncludeDispositionLinework)
                {
                    EraseEntities(database, dispositionPolylineIds, logger, "disposition linework");
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("CleanupAfterBuild error: " + ex);
            }
        }

        private static void EraseEntities(Database database, IEnumerable<ObjectId> ids, Logger logger, string label)
        {
            if (database == null || ids == null)
                return;

            var unique = ids.Where(id => !id.IsNull).Distinct().ToList();
            if (unique.Count == 0)
                return;

            using (var tr = database.TransactionManager.StartTransaction())
            {
                int erased = 0;
                foreach (var id in unique)
                {
                    try
                    {
                        var obj = tr.GetObject(id, OpenMode.ForWrite, false);
                        if (obj == null || obj.IsErased)
                            continue;

                        obj.Erase(true);
                        erased++;
                    }
                    catch
                    {
                        // Ignore failures (object may already be erased, on locked layer, etc.)
                    }
                }

                tr.Commit();

                if (erased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {erased} {label} entities");
                }
            }
        }

        private static void TryExportCadDiagnosticGeoJson(
            Database database,
            IReadOnlyList<SectionRequest> requests,
            string dllFolder,
            Logger logger)
        {
            if (database == null)
            {
                return;
            }

            try
            {
                var exportPath = ResolveCadGeoJsonPath(database, dllFolder);
                var exportDirectory = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrWhiteSpace(exportDirectory))
                {
                    Directory.CreateDirectory(exportDirectory);
                }

                var zone = ResolveSingleRequestedZone(requests);
                var features = CollectCadDiagnosticLineFeatures(database, zone);

                using (var stream = new FileStream(exportPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "FeatureCollection");
                    writer.WriteStartObject("properties");
                    writer.WriteString("source", "AtsBackgroundBuilder");
                    writer.WriteString("coordinate_space", "UTM83");
                    if (zone.HasValue)
                    {
                        writer.WriteNumber("zone", zone.Value);
                    }
                    writer.WriteEndObject();
                    writer.WriteStartArray("features");
                    foreach (var feature in features)
                    {
                        WriteCadDiagnosticFeature(writer, feature);
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                }

                logger?.WriteLine($"CAD-DIAG export: wrote {features.Count} segment(s) to {exportPath}.");
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("CAD-DIAG export failed: " + ex.Message);
            }
        }

        private static string ResolveCadGeoJsonPath(Database database, string dllFolder)
        {
            if (!string.IsNullOrWhiteSpace(CadGeoJsonExportPath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(CadGeoJsonExportPath.Trim().Trim('"'));
                return Path.GetFullPath(expanded);
            }

            if (!string.IsNullOrWhiteSpace(database?.Filename))
            {
                var drawingDirectory = Path.GetDirectoryName(database.Filename);
                if (!string.IsNullOrWhiteSpace(drawingDirectory))
                {
                    return Path.Combine(drawingDirectory, "cad_lines.geojson");
                }
            }

            var baseDirectory = string.IsNullOrWhiteSpace(dllFolder) ? Environment.CurrentDirectory : dllFolder;
            return Path.Combine(baseDirectory, "cad_lines.geojson");
        }

        private static int? ResolveSingleRequestedZone(IReadOnlyList<SectionRequest> requests)
        {
            if (requests == null || requests.Count == 0)
            {
                return null;
            }

            int? zone = null;
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var requestZone = request.Key.Zone;
                if (!zone.HasValue)
                {
                    zone = requestZone;
                    continue;
                }

                if (zone.Value != requestZone)
                {
                    return null;
                }
            }

            return zone;
        }

        private static List<CadDiagnosticLineFeature> CollectCadDiagnosticLineFeatures(Database database, int? zone)
        {
            var features = new List<CadDiagnosticLineFeature>();
            if (database == null)
            {
                return features;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in modelSpace)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!ShouldExportCadDiagnosticLayer(ent.Layer))
                    {
                        continue;
                    }

                    if (ent is Line line)
                    {
                        AddCadDiagnosticSegment(features, line.StartPoint, line.EndPoint, ent, zone);
                        continue;
                    }

                    if (ent is Polyline polyline)
                    {
                        var vertexCount = polyline.NumberOfVertices;
                        if (vertexCount < 2)
                        {
                            continue;
                        }

                        for (var i = 0; i < vertexCount; i++)
                        {
                            var next = i + 1;
                            if (next >= vertexCount)
                            {
                                if (!polyline.Closed)
                                {
                                    break;
                                }

                                next = 0;
                            }

                            AddCadDiagnosticSegment(features, polyline.GetPoint3dAt(i), polyline.GetPoint3dAt(next), ent, zone);
                        }

                        continue;
                    }

                    if (ent is Polyline2d polyline2d)
                    {
                        var vertices = new List<Point3d>();
                        foreach (ObjectId vertexId in polyline2d)
                        {
                            var vertex = tr.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                            if (vertex != null)
                            {
                                vertices.Add(vertex.Position);
                            }
                        }

                        AddPolylineSegments(features, vertices, polyline2d.Closed, ent, zone);
                        continue;
                    }

                    if (ent is Polyline3d polyline3d)
                    {
                        var vertices = new List<Point3d>();
                        foreach (ObjectId vertexId in polyline3d)
                        {
                            var vertex = tr.GetObject(vertexId, OpenMode.ForRead, false) as PolylineVertex3d;
                            if (vertex != null)
                            {
                                vertices.Add(vertex.Position);
                            }
                        }

                        AddPolylineSegments(features, vertices, polyline3d.Closed, ent, zone);
                    }
                }

                tr.Commit();
            }

            return features;
        }

        private static void AddPolylineSegments(
            List<CadDiagnosticLineFeature> features,
            List<Point3d> vertices,
            bool closed,
            Entity ent,
            int? zone)
        {
            if (features == null || vertices == null || ent == null || vertices.Count < 2)
            {
                return;
            }

            var segmentCount = closed ? vertices.Count : vertices.Count - 1;
            for (var i = 0; i < segmentCount; i++)
            {
                var next = i + 1;
                if (next >= vertices.Count)
                {
                    next = 0;
                }

                AddCadDiagnosticSegment(features, vertices[i], vertices[next], ent, zone);
            }
        }

        private static void AddCadDiagnosticSegment(
            List<CadDiagnosticLineFeature> features,
            Point3d start,
            Point3d end,
            Entity ent,
            int? zone)
        {
            if (features == null || ent == null)
            {
                return;
            }

            if (start.DistanceTo(end) <= 1e-9)
            {
                return;
            }

            var layer = NormalizeCadDiagnosticLayerName(ent.Layer);
            features.Add(new CadDiagnosticLineFeature(
                start,
                end,
                layer,
                ent.GetType().Name,
                ent.Handle.ToString(),
                ent.ColorIndex,
                zone));
        }

        private static bool ShouldExportCadDiagnosticLayer(string layer)
        {
            var normalized = NormalizeCadDiagnosticLayerName(layer);
            return
                string.Equals(normalized, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "L-QSEC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "L-QSEC-BOX", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCadDiagnosticLayerName(string layer)
        {
            return string.IsNullOrWhiteSpace(layer) ? string.Empty : layer.Trim().ToUpperInvariant();
        }

        private static void WriteCadDiagnosticFeature(Utf8JsonWriter writer, CadDiagnosticLineFeature feature)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Feature");

            writer.WriteStartObject("properties");
            writer.WriteString("layer", feature.Layer);
            writer.WriteString("entity_type", feature.EntityType);
            writer.WriteString("handle", feature.Handle);
            writer.WriteNumber("color_index", feature.ColorIndex);
            if (feature.Zone.HasValue)
            {
                writer.WriteNumber("zone", feature.Zone.Value);
            }
            writer.WriteEndObject();

            writer.WriteStartObject("geometry");
            writer.WriteString("type", "LineString");
            writer.WriteStartArray("coordinates");
            writer.WriteStartArray();
            writer.WriteNumberValue(feature.Start.X);
            writer.WriteNumberValue(feature.Start.Y);
            writer.WriteEndArray();
            writer.WriteStartArray();
            writer.WriteNumberValue(feature.End.X);
            writer.WriteNumberValue(feature.End.Y);
            writer.WriteEndArray();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        private sealed class CadDiagnosticLineFeature
        {
            public CadDiagnosticLineFeature(
                Point3d start,
                Point3d end,
                string layer,
                string entityType,
                string handle,
                int colorIndex,
                int? zone)
            {
                Start = start;
                End = end;
                Layer = layer ?? string.Empty;
                EntityType = entityType ?? string.Empty;
                Handle = handle ?? string.Empty;
                ColorIndex = colorIndex;
                Zone = zone;
            }

            public Point3d Start { get; }
            public Point3d End { get; }
            public string Layer { get; }
            public string EntityType { get; }
            public string Handle { get; }
            public int ColorIndex { get; }
            public int? Zone { get; }
        }

        private static SectionDrawResult TryPromptAndBuildSections(Editor editor, Database database, Config config, Logger logger)
        {
            if (config.UseSectionIndex)
            {
                var requests = PromptForSectionRequests(editor);
                if (requests.Count > 0)
                {
                    var result = DrawSectionsFromRequests(editor, database, requests, config, logger, false);
                    if (result.QuarterPolylineIds.Count == 0)
                    {
                        var searchFolders = BuildSectionIndexSearchFolders(config);
                        var zones = new HashSet<int>();
                        foreach (var request in requests)
                        {
                            zones.Add(request.Key.Zone);
                        }

                        var zoneList = string.Join(", ", zones);
                        editor.WriteMessage(
                            $"\nNo section outlines found in index. " +
                            $"Verify the section index files for zone(s) {zoneList} exist in {string.Join("; ", searchFolders)} " +
                            "(Master_Sections.index_Z<zone>.jsonl/.csv or Master_Sections.index.jsonl/.csv). " +
                            "See AtsBackgroundBuilder.log for details.");
                    }

                    return result;
                }
            }

            editor.WriteMessage("\nSection input required.");
            return new SectionDrawResult(
                new List<ObjectId>(),
                new List<QuarterLabelInfo>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new List<ObjectId>(),
                new Dictionary<ObjectId, int>(),
                false);
        }

        private static List<SectionRequest> PromptForSectionRequests(Editor editor)
        {
            var requests = new List<SectionRequest>();
            var zone = PromptForInt(editor, "Enter zone", 11, 1, 60);

            var addAnother = true;
            while (addAnother)
            {
                var quarter = PromptForQuarter(editor);
                if (quarter == QuarterSelection.None)
                {
                    break;
                }

                if (!TryPromptString(editor, "Enter section", out var section) ||
                    !TryPromptString(editor, "Enter township", out var township) ||
                    !TryPromptString(editor, "Enter range", out var range) ||
                    !TryPromptString(editor, "Enter meridian", out var meridian))
                {
                    break;
                }

                requests.Add(new SectionRequest(quarter, new SectionKey(zone, section, township, range, meridian)));

                var moreOptions = new PromptKeywordOptions("Add another section?")
                {
                    AllowNone = true
                };
                moreOptions.Keywords.Add("Yes");
                moreOptions.Keywords.Add("No");
                moreOptions.Keywords.Default = "No";

                var moreResult = editor.GetKeywords(moreOptions);
                addAnother = moreResult.Status == PromptStatus.OK &&
                             string.Equals(moreResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }

            return requests;
        }

        private static string PromptForClient(Editor editor, ExcelLookup lookup)
        {
            var values = lookup.Values;
            if (values.Count > 0 && values.Count <= 20)
            {
                var options = new PromptKeywordOptions("Select current client")
                {
                    AllowNone = false
                };

                foreach (var value in values)
                {
                    options.Keywords.Add(value);
                }

                var result = editor.GetKeywords(options);
                if (result.Status == PromptStatus.OK)
                {
                    return result.StringResult;
                }
            }

            if (values.Count > 0)
            {
                editor.WriteMessage("\nAvailable clients: " + string.Join(", ", values));
            }

            var prompt = new PromptStringOptions("Enter current client name: ")
            {
                AllowSpaces = true
            };
            var input = editor.GetString(prompt);
            return input.Status == PromptStatus.OK ? input.StringResult : string.Empty;
        }

        private static double PromptForDouble(Editor editor, string message, double defaultValue, double min, double max)
        {
            var options = new PromptDoubleOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true
            };

            var result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK)
            {
                return defaultValue;
            }

            var value = result.Value;
            if (value < min || value > max)
            {
                editor.WriteMessage($"\nValue out of range. Using nearest allowed value ({min} - {max}).");
                return Math.Min(Math.Max(value, min), max);
            }

            return value;
        }

        private static int PromptForInt(Editor editor, string message, int defaultValue, int min, int max)
        {
            var options = new PromptIntegerOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true,
                LowerLimit = min,
                UpperLimit = max
            };

            var result = editor.GetInteger(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        private static string MapValue(ExcelLookup lookup, string key, string fallback)
        {
            var entry = lookup.Lookup(key);
            return string.IsNullOrWhiteSpace(entry?.Value) ? fallback : entry.Value!;
        }

        private static string FormatDispNum(string? dispNum)
        {
            var regex = new Regex("^([A-Z]{3})(\\d+)");
            var match = regex.Match(dispNum ?? string.Empty);
            if (!match.Success)
            {
                return dispNum ?? string.Empty;
            }

            return match.Groups[1].Value + " " + match.Groups[2].Value;
        }

        private static string ResolveLookupPath(string lookupFolder, string configuredFileName, string dllFolder, string defaultFileName)
        {
            // If the configured name is an absolute path, use it directly.
            if (!string.IsNullOrWhiteSpace(configuredFileName) && Path.IsPathRooted(configuredFileName) && File.Exists(configuredFileName))
                return configuredFileName;

            var fileName = string.IsNullOrWhiteSpace(configuredFileName) ? defaultFileName : configuredFileName;

            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(lookupFolder))
                candidates.Add(Path.Combine(lookupFolder, fileName));

            if (!string.IsNullOrWhiteSpace(dllFolder))
                candidates.Add(Path.Combine(dllFolder, fileName));

            // Fall back to current working directory if needed
            candidates.Add(Path.Combine(Environment.CurrentDirectory, fileName));

            foreach (var p in candidates)
            {
                try
                {
                    if (File.Exists(p))
                        return p;
                }
                catch
                {
                    // ignore
                }
            }

            // If nothing exists, return the first candidate so the logger prints a useful "not found" path.
            return candidates.FirstOrDefault() ?? Path.Combine(dllFolder ?? "", fileName);
        }

        private static bool PurposeRequiresWidth(string purpose, Config config)
        {
            if (string.IsNullOrWhiteSpace(purpose))
                return false;

            var norm = NormalizePurposeCode(purpose);
            var list = config.WidthRequiredPurposeCodes ?? Array.Empty<string>();

            foreach (var item in list)
            {
                if (NormalizePurposeCode(item) == norm)
                    return true;
            }

            return false;
        }

        private static string NormalizePurposeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // Trim, uppercase, and collapse all whitespace to single spaces
            var s = value.Trim().ToUpperInvariant();
            var chars = new List<char>(s.Length);
            bool prevSpace = false;

            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!prevSpace)
                    {
                        chars.Add(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    chars.Add(ch);
                    prevSpace = false;
                }
            }

            return new string(chars.ToArray());
        }

        private static string ToTitleCaseWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            // TitleCase, then common-word cleanup ("and" stays lower-case)
            var lower = NormalizePurposeCode(value).ToLowerInvariant();
            var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);

            title = title.Replace(" And ", " and ");
            title = title.Replace(" Of ", " of ");
            title = title.Replace(" The ", " the ");
            return title;
        }

        private static bool IsWellSitePurpose(string purpose)
        {
            var normalized = NormalizePurposeCode(purpose);
            return string.Equals(normalized, "WELL SITE", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalized, "WELLSITE", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseSectionNumber(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return 0;
            }

            var match = Regex.Match(section, "\\d+");
            if (!match.Success)
            {
                return 0;
            }

            return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        // Targeted debug trace for known range-edge layer drift.
        private static readonly Point2d LayerTraceSegmentStart = new Point2d(530925.626, 5980315.643);
        private static readonly Point2d LayerTraceSegmentEnd = new Point2d(530919.985, 5981120.856);
        private const double LayerTraceEndpointTol = 2.5;
        private const double LayerTraceMidpointTol = 2.5;
        private const double LayerTraceLengthTol = 30.0;

        private static bool IsTargetLayerTraceSegment(Point2d a, Point2d b)
        {
            var directEndpointMatch =
                (a.GetDistanceTo(LayerTraceSegmentStart) <= LayerTraceEndpointTol &&
                 b.GetDistanceTo(LayerTraceSegmentEnd) <= LayerTraceEndpointTol) ||
                (a.GetDistanceTo(LayerTraceSegmentEnd) <= LayerTraceEndpointTol &&
                 b.GetDistanceTo(LayerTraceSegmentStart) <= LayerTraceEndpointTol);
            if (directEndpointMatch)
            {
                return true;
            }

            var targetMid = Midpoint(LayerTraceSegmentStart, LayerTraceSegmentEnd);
            var segMid = Midpoint(a, b);
            if (DistancePointToSegment(targetMid, a, b) > LayerTraceMidpointTol ||
                DistancePointToSegment(segMid, LayerTraceSegmentStart, LayerTraceSegmentEnd) > LayerTraceMidpointTol)
            {
                return false;
            }

            var targetLen = LayerTraceSegmentStart.GetDistanceTo(LayerTraceSegmentEnd);
            var segLen = a.GetDistanceTo(b);
            return Math.Abs(segLen - targetLen) <= LayerTraceLengthTol;
        }

        private static bool TryGetAtsSectionGridPosition(int section, out int row, out int col)
        {
            row = -1;
            col = -1;
            if (section < 1 || section > 36)
            {
                return false;
            }

            // ATS/DLS section grid (6x6 snake pattern, north at top).
            // Alberta numbering starts at the southeast corner:
            // 6  5  4  3  2  1
            // 7  8  9 10 11 12
            // 18 17 16 15 14 13
            // 19 20 21 22 23 24
            // 30 29 28 27 26 25
            // 31 32 33 34 35 36
            var rows = new[]
            {
                new[] { 31, 32, 33, 34, 35, 36 },
                new[] { 30, 29, 28, 27, 26, 25 },
                new[] { 19, 20, 21, 22, 23, 24 },
                new[] { 18, 17, 16, 15, 14, 13 },
                new[] { 7, 8, 9, 10, 11, 12 },
                new[] { 6, 5, 4, 3, 2, 1 }
            };

            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (rows[r][c] == section)
                    {
                        row = r;
                        col = c;
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<LsdCellInfo> BuildSectionLsdCells(Transaction transaction, Dictionary<ObjectId, int> sectionNumberById)
        {
            var cells = new List<LsdCellInfo>();
            if (sectionNumberById == null || sectionNumberById.Count == 0)
            {
                return cells;
            }

            foreach (var pair in sectionNumberById)
            {
                if (pair.Key.IsNull || pair.Key.IsErased)
                {
                    continue;
                }

                var section = transaction.GetObject(pair.Key, OpenMode.ForRead) as Polyline;
                if (section == null || !section.Closed || section.NumberOfVertices < 3)
                {
                    continue;
                }

                BuildLsdCellsForSection(section, pair.Value, cells);
            }

            return cells;
        }

        private static void BuildLsdCellsForSection(Polyline section, int sectionNumber, List<LsdCellInfo> destination)
        {
            var outline = GetPolylinePoints(section);
            if (outline.Count < 3)
            {
                return;
            }

            QuarterAnchors anchors;
            if (!TryGetQuarterAnchors(section, out anchors))
            {
                anchors = GetFallbackAnchors(section);
            }

            var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
            var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));

            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) ||
                !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                !TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
            {
                var ext = section.GeometricExtents;
                sw = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                se = new Point2d(ext.MaxPoint.X, ext.MinPoint.Y);
                nw = new Point2d(ext.MinPoint.X, ext.MaxPoint.Y);
                ne = new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y);
            }

            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    var u0 = col / 4.0;
                    var u1 = (col + 1) / 4.0;
                    var t0 = row / 4.0;
                    var t1 = (row + 1) / 4.0;

                    var sample = BilinearPoint(sw, se, nw, ne, 0.5 * (u0 + u1), 0.5 * (t0 + t1));
                    var points = new List<Point2d>(outline);

                    var leftSouth = BilinearPoint(sw, se, nw, ne, u0, 0.0);
                    var leftNorth = BilinearPoint(sw, se, nw, ne, u0, 1.0);
                    var rightSouth = BilinearPoint(sw, se, nw, ne, u1, 0.0);
                    var rightNorth = BilinearPoint(sw, se, nw, ne, u1, 1.0);
                    var bottomWest = BilinearPoint(sw, se, nw, ne, 0.0, t0);
                    var bottomEast = BilinearPoint(sw, se, nw, ne, 1.0, t0);
                    var topWest = BilinearPoint(sw, se, nw, ne, 0.0, t1);
                    var topEast = BilinearPoint(sw, se, nw, ne, 1.0, t1);

                    var keepLeft = GetSideSign(leftSouth, leftNorth, sample);
                    var keepRight = GetSideSign(rightSouth, rightNorth, sample);
                    var keepBottom = GetSideSign(bottomWest, bottomEast, sample);
                    var keepTop = GetSideSign(topWest, topEast, sample);

                    if (keepLeft == 0 || keepRight == 0 || keepBottom == 0 || keepTop == 0)
                    {
                        continue;
                    }

                    points = ClipPolygon(points, leftSouth, leftNorth, keepLeft);
                    points = ClipPolygon(points, rightSouth, rightNorth, keepRight);
                    points = ClipPolygon(points, bottomWest, bottomEast, keepBottom);
                    points = ClipPolygon(points, topWest, topEast, keepTop);

                    var cell = BuildPolylineFromPoints(points);
                    if (cell == null)
                    {
                        continue;
                    }

                    var lsd = GetLsdNumber(row, col);
                    destination.Add(new LsdCellInfo(cell, lsd, sectionNumber));
                }
            }
        }

        private static int GetLsdNumber(int rowFromSouth, int colFromWest)
        {
            if ((rowFromSouth % 2) == 0)
            {
                return (rowFromSouth * 4) + (4 - colFromWest);
            }

            return (rowFromSouth * 4) + (colFromWest + 1);
        }

        private static Point2d BilinearPoint(Point2d sw, Point2d se, Point2d nw, Point2d ne, double u, double t)
        {
            var south = sw + ((se - sw) * u);
            var north = nw + ((ne - nw) * u);
            return south + ((north - south) * t);
        }

        private static string GetDominantLsdSectionToken(Polyline disposition, List<LsdCellInfo> lsdCells)
        {
            if (disposition == null || lsdCells == null || lsdCells.Count == 0)
            {
                return string.Empty;
            }

            double bestArea = 0.0;
            int bestLsd = 0;
            int bestSection = 0;

            Extents3d dispExtents;
            try
            {
                dispExtents = disposition.GeometricExtents;
            }
            catch
            {
                return string.Empty;
            }

            foreach (var cell in lsdCells)
            {
                if (cell.Cell == null)
                {
                    continue;
                }

                try
                {
                    if (!GeometryUtils.ExtentsIntersect(dispExtents, cell.Cell.GeometricExtents))
                    {
                        continue;
                    }
                }
                catch
                {
                    // ignore extents failures and try geometric intersection directly
                }

                var overlapArea = GetIntersectionArea(disposition, cell.Cell);
                if (overlapArea > bestArea)
                {
                    bestArea = overlapArea;
                    bestLsd = cell.Lsd;
                    bestSection = cell.Section;
                }
            }

            return bestArea > 1e-6 && bestLsd > 0 && bestSection > 0
                ? $"{bestLsd}-{bestSection}"
                : string.Empty;
        }

        private static List<SectionSpatialInfo> BuildSectionSpatialInfos(Transaction transaction, Dictionary<ObjectId, int> sectionNumberById)
        {
            var infos = new List<SectionSpatialInfo>();
            if (sectionNumberById == null || sectionNumberById.Count == 0)
            {
                return infos;
            }

            foreach (var pair in sectionNumberById)
            {
                if (pair.Key.IsNull || pair.Key.IsErased)
                {
                    continue;
                }

                var section = transaction.GetObject(pair.Key, OpenMode.ForRead) as Polyline;
                if (section == null || !section.Closed || section.NumberOfVertices < 3)
                {
                    continue;
                }

                if (TryCreateSectionSpatialInfo(section, pair.Value, out var info))
                {
                    infos.Add(info);
                }
            }

            return infos;
        }

        private static List<SectionSpatialInfo> LoadSupplementalSectionSpatialInfos(
            IReadOnlyList<SectionRequest> requests,
            Config config,
            Logger logger)
        {
            var infos = new List<SectionSpatialInfo>();
            if (requests == null || requests.Count == 0)
            {
                return infos;
            }

            var searchFolders = BuildSectionIndexSearchFolders(config);
            var townshipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var request in requests)
            {
                var key = request.Key;
                var townshipKey = $"{key.Zone}|{NormalizeNumberToken(key.Meridian)}|{NormalizeNumberToken(key.Range)}|{NormalizeNumberToken(key.Township)}";
                if (!townshipKeys.Add(townshipKey))
                {
                    continue;
                }

                for (var section = 1; section <= 36; section++)
                {
                    var sectionKey = new SectionKey(
                        key.Zone,
                        section.ToString(CultureInfo.InvariantCulture),
                        key.Township,
                        key.Range,
                        key.Meridian);
                    var keyId = BuildSectionKeyId(sectionKey);
                    if (!sectionKeys.Add(keyId))
                    {
                        continue;
                    }

                    if (!TryLoadSectionOutline(searchFolders, sectionKey, logger, out var outline))
                    {
                        continue;
                    }

                    if (TryCreateSectionSpatialInfo(outline, section, out var info))
                    {
                        infos.Add(info);
                    }
                }
            }

            if (infos.Count > 0)
            {
                logger.WriteLine($"Loaded {infos.Count} supplemental section outlines for wellsite section matching.");
            }

            return infos;
        }

        private static bool TryCreateSectionSpatialInfo(
            SectionOutline outline,
            int sectionNumber,
            [NotNullWhen(true)] out SectionSpatialInfo? info)
        {
            info = null;
            if (outline == null || outline.Vertices == null || outline.Vertices.Count < 3)
            {
                return false;
            }

            var section = new Polyline(outline.Vertices.Count)
            {
                Closed = outline.Closed
            };
            for (var i = 0; i < outline.Vertices.Count; i++)
            {
                section.AddVertexAt(i, outline.Vertices[i], 0, 0, 0);
            }

            try
            {
                return TryCreateSectionSpatialInfo(section, sectionNumber, out info);
            }
            finally
            {
                section.Dispose();
            }
        }

        private static bool TryCreateSectionSpatialInfo(
            Polyline section,
            int sectionNumber,
            [NotNullWhen(true)] out SectionSpatialInfo? info)
        {
            info = null;
            var clone = section.Clone() as Polyline;
            if (clone == null)
            {
                return false;
            }

            try
            {
                QuarterAnchors anchors;
                if (!TryGetQuarterAnchors(clone, out anchors))
                {
                    anchors = GetFallbackAnchors(clone);
                }

                var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));

                if (!TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                    !TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.SouthEast, out var se) ||
                    !TryGetQuarterCorner(clone, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw))
                {
                    var ext = clone.GeometricExtents;
                    sw = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                    se = new Point2d(ext.MaxPoint.X, ext.MinPoint.Y);
                    nw = new Point2d(ext.MinPoint.X, ext.MaxPoint.Y);
                }

                var width = Math.Abs((se - sw).DotProduct(eastUnit));
                var height = Math.Abs((nw - sw).DotProduct(northUnit));
                if (width <= 1e-6 || height <= 1e-6)
                {
                    clone.Dispose();
                    return false;
                }

                info = new SectionSpatialInfo(clone, sectionNumber, sw, eastUnit, northUnit, width, height);
                return true;
            }
            catch
            {
                clone.Dispose();
                return false;
            }
        }

        private static string GetDominantLsdSectionTokenBySection(Polyline disposition, List<SectionSpatialInfo> sections)
        {
            if (disposition == null || sections == null || sections.Count == 0)
            {
                return string.Empty;
            }

            SectionSpatialInfo? best = null;
            double bestArea = 0.0;

            foreach (var section in sections)
            {
                var area = GetIntersectionArea(disposition, section.SectionPolyline);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = section;
                }
            }

            if (best == null || bestArea <= 1e-6)
            {
                return string.Empty;
            }

            if (!TryGetRepresentativePointInSection(disposition, best.SectionPolyline, out var point))
            {
                return string.Empty;
            }

            var rowCol = GetLsdRowCol(point, best);
            if (rowCol == null)
            {
                return string.Empty;
            }

            var lsd = GetLsdNumber(rowCol.Value.row, rowCol.Value.col);
            return (lsd > 0 && best.Section > 0) ? $"{lsd}-{best.Section}" : string.Empty;
        }

        private static bool TryGetRepresentativePointInSection(Polyline disposition, Polyline section, out Point2d point)
        {
            point = default;
            if (disposition == null || section == null)
            {
                return false;
            }

            if (GeometryUtils.TryIntersectPolylines(disposition, section, out var pieces) && pieces.Count > 0)
            {
                Polyline? best = null;
                double bestArea = 0.0;
                foreach (var p in pieces)
                {
                    double area = 0.0;
                    try { area = Math.Abs(p.Area); } catch { }
                    if (area > bestArea)
                    {
                        best?.Dispose();
                        best = p;
                        bestArea = area;
                    }
                    else
                    {
                        p.Dispose();
                    }
                }

                if (best != null)
                {
                    try
                    {
                        point = GeometryUtils.GetSafeInteriorPoint(best);
                        return true;
                    }
                    finally
                    {
                        best.Dispose();
                    }
                }
            }

            return false;
        }

        private static (int row, int col)? GetLsdRowCol(Point2d point, SectionSpatialInfo section)
        {
            var v = point - section.SouthWest;
            var u = v.DotProduct(section.EastUnit) / section.Width;
            var t = v.DotProduct(section.NorthUnit) / section.Height;

            if (double.IsNaN(u) || double.IsInfinity(u) || double.IsNaN(t) || double.IsInfinity(t))
            {
                return null;
            }

            u = Math.Max(0.0, Math.Min(0.999999, u));
            t = Math.Max(0.0, Math.Min(0.999999, t));

            var col = (int)Math.Floor(u * 4.0);
            var row = (int)Math.Floor(t * 4.0);

            if (col < 0 || col > 3 || row < 0 || row > 3)
            {
                return null;
            }

            return (row, col);
        }

        private static string GetSectionOverlapDebugString(Polyline disposition, List<SectionSpatialInfo> sections)
        {
            if (disposition == null || sections == null || sections.Count == 0)
            {
                return "(no section infos)";
            }

            var rows = new List<(int section, double area)>();
            foreach (var section in sections)
            {
                var area = GetIntersectionArea(disposition, section.SectionPolyline);
                if (area > 1e-6)
                {
                    rows.Add((section.Section, area));
                }
            }

            if (rows.Count == 0)
            {
                return "(no overlaps > 0)";
            }

            rows.Sort((a, b) => b.area.CompareTo(a.area));
            var top = rows.Take(3).Select(r => $"SEC{r.section}={r.area:0.###}");
            return string.Join(", ", top);
        }

        private static string GetPointBasedLsdSectionToken(Polyline disposition, List<SectionSpatialInfo> sections, out string debug)
        {
            debug = "no-sections";
            if (disposition == null || sections == null || sections.Count == 0)
            {
                return string.Empty;
            }

            var samplePoints = new List<Point2d>();
            try
            {
                samplePoints.Add(GeometryUtils.GetSafeInteriorPoint(disposition));
            }
            catch
            {
                // ignore
            }

            for (var i = 0; i < disposition.NumberOfVertices; i++)
            {
                samplePoints.Add(disposition.GetPoint2dAt(i));
            }

            if (samplePoints.Count == 0)
            {
                debug = "no-samples";
                return string.Empty;
            }

            SectionSpatialInfo? bestSection = null;
            int bestHits = -1;
            Point2d bestPoint = default;

            foreach (var section in sections)
            {
                int hits = 0;
                Point2d firstHit = default;
                bool hasFirst = false;

                foreach (var p in samplePoints)
                {
                    if (GeometryUtils.IsPointInsidePolyline(section.SectionPolyline, p))
                    {
                        hits++;
                        if (!hasFirst)
                        {
                            firstHit = p;
                            hasFirst = true;
                        }
                    }
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestSection = section;
                    bestPoint = hasFirst ? firstHit : samplePoints[0];
                }
            }

            if (bestSection == null || bestHits <= 0)
            {
                debug = "no-section-hit";
                return string.Empty;
            }

            var rowCol = GetLsdRowCol(bestPoint, bestSection);
            if (rowCol == null)
            {
                debug = $"section={bestSection.Section} hits={bestHits} rowcol=none";
                return string.Empty;
            }

            var lsd = GetLsdNumber(rowCol.Value.row, rowCol.Value.col);
            debug = $"section={bestSection.Section} hits={bestHits} point=({bestPoint.X:0.###},{bestPoint.Y:0.###}) row={rowCol.Value.row} col={rowCol.Value.col} lsd={lsd}";
            return (lsd > 0 && bestSection.Section > 0) ? $"{lsd}-{bestSection.Section}" : string.Empty;
        }

        private static double GetIntersectionArea(Polyline a, Polyline b)
        {
            if (a == null || b == null)
            {
                return 0.0;
            }

            if (!GeometryUtils.TryIntersectPolylines(a, b, out var pieces) || pieces == null || pieces.Count == 0)
            {
                return 0.0;
            }

            double area = 0.0;
            foreach (var piece in pieces)
            {
                try
                {
                    area += Math.Abs(piece.Area);
                }
                catch
                {
                    // ignore individual piece area failures
                }
                finally
                {
                    piece.Dispose();
                }
            }

            return area;
        }

        private static void DisposeLsdCells(List<LsdCellInfo> cells)
        {
            if (cells == null)
            {
                return;
            }

            foreach (var cell in cells)
            {
                try
                {
                    cell.Cell?.Dispose();
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

        private static void DisposeSectionInfos(List<SectionSpatialInfo> infos)
        {
            if (infos == null)
            {
                return;
            }

            foreach (var info in infos)
            {
                try
                {
                    info.SectionPolyline?.Dispose();
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }

        private static IEnumerable<Polyline> GenerateQuarters(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            yield return CreateRectangle(minX, minY, midX, midY);
            yield return CreateRectangle(midX, minY, maxX, midY);
            yield return CreateRectangle(minX, midY, midX, maxY);
            yield return CreateRectangle(midX, midY, maxX, maxY);
        }

        private static Polyline CreateRectangle(double minX, double minY, double maxX, double maxY)
        {
            var polyline = new Polyline(4)
            {
                Closed = true
            };

            polyline.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);

            return polyline;
        }

        private static SectionDrawResult DrawSectionsFromRequests(
            Editor editor,
            Database database,
            List<SectionRequest> requests,
            Config config,
            Logger logger,
            bool drawLsds)
        {
            var timer = Stopwatch.StartNew();
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }
            // We always draw the full section outline (ATS fabric on) but may label only specific quarters.
            var labelQuarterIds = new HashSet<ObjectId>();
            var labelQuarterInfos = new List<QuarterLabelInfo>();
            var lsdQuarterInfos = new List<QuarterLabelInfo>();
            var lsdQuarterInfoIds = new HashSet<ObjectId>();
            var allQuarterIds = new HashSet<ObjectId>();
            var quarterHelperIds = new HashSet<ObjectId>();
            var sectionLabelIds = new HashSet<ObjectId>();
            var sectionIds = new HashSet<ObjectId>();
            var contextSectionIds = new HashSet<ObjectId>();
            var contextRuleSectionInfos = new List<QuarterLabelInfo>();
            var contextSectionOutlineHelperIds = new List<ObjectId>();
            var sectionNumberById = new Dictionary<ObjectId, int>();
            var createdSections = new Dictionary<string, SectionBuildResult>(StringComparer.OrdinalIgnoreCase);
            var createdSectionQuarterSecTypes = new Dictionary<string, Dictionary<QuarterSelection, string>>(StringComparer.OrdinalIgnoreCase);
            var searchFolders = BuildSectionIndexSearchFolders(config);
            var inferredSecTypes = InferSectionTypes(requests, searchFolders, logger);
            var inferredQuarterSecTypes = InferQuarterSectionTypes(requests, searchFolders, logger);
            logger.WriteLine($"TIMING DrawSectionsFromRequests: inference completed in {timer.ElapsedMilliseconds} ms");

            foreach (var request in requests)
            {
                var keyId = BuildSectionKeyId(request.Key);
                var resolvedSecType = ResolveSectionType(request.Key, request.SecType, inferredSecTypes);
                Dictionary<QuarterSelection, string>? resolvedQuarterSecTypes = null;
                if (!createdSections.TryGetValue(keyId, out var buildResult))
                {
                    if (!TryLoadSectionOutline(searchFolders, request.Key, logger, out var outline))
                    {
                        continue;
                    }

                    resolvedQuarterSecTypes = ResolveQuarterSectionTypes(request.Key, resolvedSecType, inferredQuarterSecTypes);
                    // Draw LSD geometry in the final stage after road-allowance/section cleanup.
                    // This keeps LSD endpoint decisions deterministic against final hard boundaries.
                    buildResult = DrawSectionFromIndex(editor, database, outline, request.Key, drawLsds: false, resolvedSecType, resolvedQuarterSecTypes);
                    createdSections[keyId] = buildResult;
                    createdSectionQuarterSecTypes[keyId] = resolvedQuarterSecTypes;
                    sectionIds.Add(buildResult.SectionPolylineId);
                    sectionNumberById[buildResult.SectionPolylineId] = ParseSectionNumber(request.Key.Section);
                }
                else if (!createdSectionQuarterSecTypes.TryGetValue(keyId, out resolvedQuarterSecTypes))
                {
                    resolvedQuarterSecTypes = ResolveQuarterSectionTypes(request.Key, resolvedSecType, inferredQuarterSecTypes);
                    createdSectionQuarterSecTypes[keyId] = resolvedQuarterSecTypes;
                }

                if (resolvedQuarterSecTypes == null)
                {
                    resolvedQuarterSecTypes = ResolveQuarterSectionTypes(request.Key, resolvedSecType, inferredQuarterSecTypes);
                    createdSectionQuarterSecTypes[keyId] = resolvedQuarterSecTypes;
                }

                // Track all quarter geometry we created (always removed during cleanup).
                foreach (var quarterId in buildResult.QuarterPolylineIds.Values)
                    allQuarterIds.Add(quarterId);
                foreach (var pair in buildResult.QuarterPolylineIds)
                {
                    if (!lsdQuarterInfoIds.Add(pair.Value))
                    {
                        continue;
                    }

                    var quarterSecType = ResolveQuarterSecTypeForQuarter(resolvedQuarterSecTypes, pair.Key, resolvedSecType);
                    lsdQuarterInfos.Add(new QuarterLabelInfo(pair.Value, request.Key, pair.Key, quarterSecType, buildResult.SectionPolylineId));
                }

                foreach (var helperId in buildResult.QuarterHelperEntityIds)
                    quarterHelperIds.Add(helperId);

                if (!buildResult.SectionLabelEntityId.IsNull)
                    sectionLabelIds.Add(buildResult.SectionLabelEntityId);

                // Track the quarters that should actually be labelled.
                foreach (var quarter in ExpandQuarterSelections(request.Quarter))
                {
                    if (buildResult.QuarterPolylineIds.TryGetValue(quarter, out var qid))
                    {
                        if (labelQuarterIds.Add(qid))
                        {
                            var quarterSecType = ResolveQuarterSecTypeForQuarter(resolvedQuarterSecTypes, quarter, resolvedSecType);
                            labelQuarterInfos.Add(new QuarterLabelInfo(qid, request.Key, quarter, quarterSecType, buildResult.SectionPolylineId));
                        }
                    }
                }
            }

            logger.WriteLine($"TIMING DrawSectionsFromRequests: requested sections drawn in {timer.ElapsedMilliseconds} ms");

            var requestedScopeIds = new HashSet<ObjectId>(sectionIds.Where(id => !id.IsNull));
            if (requestedScopeIds.Count == 0)
            {
                foreach (var qid in labelQuarterIds)
                {
                    if (!qid.IsNull)
                    {
                        requestedScopeIds.Add(qid);
                    }
                }
            }

            foreach (var id in DrawAdjoiningSectionsForContext(
                database,
                searchFolders,
                requests,
                requestedScopeIds,
                inferredSecTypes,
                inferredQuarterSecTypes,
                logger,
                contextRuleSectionInfos,
                contextSectionOutlineHelperIds))
            {
                contextSectionIds.Add(id);
            }

            foreach (var id in contextSectionOutlineHelperIds)
            {
                if (!id.IsNull)
                {
                    allQuarterIds.Add(id);
                }
            }

            var ruleScopeIds = new HashSet<ObjectId>(requestedScopeIds);
            foreach (var contextOutlineId in contextSectionOutlineHelperIds)
            {
                if (!contextOutlineId.IsNull)
                {
                    ruleScopeIds.Add(contextOutlineId);
                }
            }

            CleanupContextSectionOverlaps(database, contextSectionIds, logger);
            logger.WriteLine("Cleanup: deferred context 100m trim until final geometry stage.");
            logger.WriteLine($"TIMING DrawSectionsFromRequests: context sections staged in {timer.ElapsedMilliseconds} ms");

            var generatedRoadAllowanceIds = new HashSet<ObjectId>();
            var activeSectionKeyIds = new HashSet<string>(
                requests.Select(r => BuildSectionKeyId(r.Key)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var contextInfo in contextRuleSectionInfos)
            {
                if (contextInfo == null)
                {
                    continue;
                }

                activeSectionKeyIds.Add(BuildSectionKeyId(contextInfo.SectionKey));
            }

            foreach (var id in DrawRoadAllowanceGapOffsetLines(
                database,
                searchFolders,
                requests,
                ruleScopeIds,
                inferredSecTypes,
                inferredQuarterSecTypes,
                logger,
                activeSectionKeyIds))
            {
                quarterHelperIds.Add(id);
                generatedRoadAllowanceIds.Add(id);
            }

            // Canonical simplified road-allowance mode:
            // - never move existing lines
            // - only add 20.11 connector extensions to satisfy orthogonal endpoint rules
            // - keep 30.16 as generated (no endpoint extension pass)
            logger.WriteLine("Cleanup: canonical RA mode enabled; legacy RA extension/move passes are skipped.");
            CleanupGeneratedRoadAllowanceOverlaps(database, generatedRoadAllowanceIds, logger);
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

            var originalRangeEdgeSecAnchors = SnapshotOriginalRangeEdgeSecRoadAllowanceAnchors(
                database,
                ruleScopeIds,
                generatedRoadAllowanceIds,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "snapshot-original-range-edge-sec", logger);

            NormalizeUsecLayersToThreeBands(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                generatedRoadAllowanceIds,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-usec-three-bands-1", logger);
            var quarterInfosForRoadAllowanceRules = labelQuarterInfos
                .Concat(contextRuleSectionInfos)
                .Where(info => info != null)
                .ToList();
            ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(
                database,
                searchFolders,
                quarterInfosForRoadAllowanceRules,
                generatedRoadAllowanceIds,
                logger);
            CleanupDuplicateBlindLineSegments(database, ruleScopeIds, logger);
            logger.WriteLine("Cleanup: context 100m trim deferred to final stage; context snap/stitch/seam-heal passes skipped in canonical RA mode.");
            NormalizeGeneratedRoadAllowanceLayers(database, generatedRoadAllowanceIds, logger);
            NormalizeShortRoadAllowanceLayersByNeighborhood(database, ruleScopeIds, generatedRoadAllowanceIds, logger);
            NormalizeHorizontalSecRoadAllowanceLayers(database, ruleScopeIds, generatedRoadAllowanceIds, logger);
            NormalizeBottomTownshipBoundaryLayers(database, ruleScopeIds, generatedRoadAllowanceIds, quarterInfosForRoadAllowanceRules, logger);
            NormalizeThirtyEighteenCorridorLayers(database, ruleScopeIds, logger);
            // TODO: unresolved WIP - R/A layer mix/match still occurs on corridors perpendicular
            // to township/range switch boundaries. Keep disabled range-edge relayer until deterministic fix.
            CleanupDuplicateBlindLineSegments(database, ruleScopeIds, logger);
            logger.WriteLine("Cleanup: legacy SW/NW/simple-west/stop-rule passes skipped in canonical RA mode; SE east-boundary bridge pass enabled.");
            NormalizeBlindLineLayersBySecConnections(database, ruleScopeIds, logger);
            NormalizeUsecLayersToThreeBands(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                generatedRoadAllowanceIds,
                logger);
            NormalizeUsecCollinearComponentLayerConsistency(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-usec-collinear-consistency", logger);
            NormalizeWestRoadAllowanceBandsForKnownSections(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-west-ra-bands-1", logger);
            NormalizeUsecLayersBySectionEdgeOffsets(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-section-edge-relayer-1", logger);
            NormalizeWestRoadAllowanceBandsForKnownSections(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-west-ra-bands-1b", logger);
            ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(
                database,
                ruleScopeIds,
                generatedRoadAllowanceIds,
                originalRangeEdgeSecAnchors,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-range-edge-reapply-1", logger);
            logger.WriteLine("Cleanup: context endpoint snap/stitch disabled (context is build-adjoining + 100m trim only).");
            TrimContextSectionsToBufferedWindows(database, contextSectionIds, requestedScopeIds, logger);
            if (generatedRoadAllowanceIds.Count > 0)
            {
                // "Build first, trim last": generated RA lines are now trimmed in the final stage
                // so surrounding-context behavior matches regular section construction.
                TrimContextSectionsToBufferedWindows(database, generatedRoadAllowanceIds.ToHashSet(), requestedScopeIds, logger);
            }
            // Keep final endpoint correction narrow and deterministic:
            // enforce only explicit 0/20 dangling-to-3018 cleanup after final trim.
            ConnectDanglingUsecZeroTwentyEndpoints(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            // Final deterministic relayer + reconnect pass after endpoint moves keeps
            // 0/20/30 assignment and cross-intersection continuity consistent on partial builds.
            NormalizeUsecLayersBySectionEdgeOffsets(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-section-edge-relayer-2", logger);
            NormalizeWestRoadAllowanceBandsForKnownSections(
                database,
                ruleScopeIds,
                sectionNumberByPolylineIdForUsec,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-west-ra-bands-2b", logger);
            ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(
                database,
                ruleScopeIds,
                generatedRoadAllowanceIds,
                originalRangeEdgeSecAnchors,
                logger);
            TraceTargetLayerSegmentState(database, ruleScopeIds, "after-range-edge-reapply-2", logger);
            // Priority rule: baseline township seam allowances must remain L-SEC
            // even if earlier relayer passes classify them differently.
            NormalizeBottomTownshipBoundaryLayers(
                database,
                ruleScopeIds,
                generatedRoadAllowanceIds,
                quarterInfosForRoadAllowanceRules,
                logger);
            ConnectDanglingUsecZeroTwentyEndpoints(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            EnforceSectionLineNoCrossingRules(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            TrimZeroTwentyPassThroughExtensions(database, requestedScopeIds, logger);
            ResolveZeroTwentyOverlapByEndpointIntersection(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            EnforceSecLineEndpointsOnHardSectionBoundaries(database, requestedScopeIds, logger);
            EnforceQuarterLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            EnforceBlindLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            logger.WriteLine("Cleanup: section geometry finalized (SEC/QSEC/blind endpoint passes complete); deferred LSD draw begins.");
            if (drawLsds)
            {
                DrawDeferredLsdSubdivisionLines(database, lsdQuarterInfos, logger);
                EnforceLsdLineEndpointsOnHardSectionBoundaries(database, requestedScopeIds, logger);
            }
            logger.WriteLine("Cleanup: final endpoint convergence pass begins (all endpoint targets recalculated from final geometry).");
            ConnectDanglingUsecZeroTwentyEndpoints(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            EnforceSectionLineNoCrossingRules(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            TrimZeroTwentyPassThroughExtensions(database, requestedScopeIds, logger);
            ResolveZeroTwentyOverlapByEndpointIntersection(database, requestedScopeIds, logger);
            CleanupOverlappingZeroTwentySectionLines(database, requestedScopeIds, logger);
            EnforceSecLineEndpointsOnHardSectionBoundaries(database, requestedScopeIds, logger);
            EnforceQuarterLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            EnforceBlindLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            if (drawLsds)
            {
                EnforceLsdLineEndpointsOnHardSectionBoundaries(database, requestedScopeIds, logger);
                RebuildLsdLabelsAtFinalIntersections(database, lsdQuarterInfos, logger);
            }
            logger.WriteLine("Cleanup: final endpoint convergence pass complete.");
            logger.WriteLine($"TIMING DrawSectionsFromRequests: road allowances processed in {timer.ElapsedMilliseconds} ms");

            if (EnableBufferedQuarterWindowDrawing)
            {
                DrawBufferedQuarterWindowsOnDefpoints(database, requestedScopeIds, 100.0, logger);
            }
            else
            {
                logger.WriteLine("DEFPOINTS 100m buffer drawing skipped (ATSBUILD_DRAW_100M_BUFFER != 1).");
            }
            logger.WriteLine($"TIMING DrawSectionsFromRequests: total {timer.ElapsedMilliseconds} ms");

            return new SectionDrawResult(
                labelQuarterIds.ToList(),
                labelQuarterInfos,
                allQuarterIds.ToList(),
                quarterHelperIds.ToList(),
                sectionIds.ToList(),
                contextSectionIds.ToList(),
                sectionLabelIds.ToList(),
                sectionNumberById,
                true);
        }

        private static List<ObjectId> DrawAdjoiningSectionsForContext(
            Database database,
            IReadOnlyList<string> searchFolders,
            IReadOnlyList<SectionRequest> requests,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyDictionary<string, string> inferredSecTypes,
            IReadOnlyDictionary<string, string> inferredQuarterSecTypes,
            Logger logger,
            List<QuarterLabelInfo>? contextRuleSectionInfos = null,
            List<ObjectId>? contextSectionOutlineHelperIds = null)
        {
            var drawnIds = new List<ObjectId>();
            if (database == null || searchFolders == null || requests == null || requestedQuarterIds == null)
            {
                return drawnIds;
            }

            var rawClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(rawClipWindows);
            if (clipWindows.Count == 0)
            {
                return drawnIds;
            }

            bool IntersectsAnyClipWindow(Extents3d extents)
            {
                for (var wi = 0; wi < clipWindows.Count; wi++)
                {
                    if (GeometryUtils.ExtentsIntersect(extents, clipWindows[wi]))
                    {
                        return true;
                    }
                }

                return false;
            }

            string BuildGridKey(int zone, string meridian, int globalX, int globalY)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3}",
                    zone,
                    NormalizeNumberToken(meridian),
                    globalX,
                    globalY);
            }

            var requestedGridKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                if (request == null ||
                    !TryParsePositiveToken(request.Key.Range, out var rangeNum) ||
                    !TryParsePositiveToken(request.Key.Township, out var townshipNum))
                {
                    continue;
                }

                var sectionNumber = ParseSectionNumber(request.Key.Section);
                if (!TryGetAtsSectionGridPosition(sectionNumber, out var row, out var col))
                {
                    continue;
                }

                var globalX = (-rangeNum * 6) + col;
                var globalY = (townshipNum * 6) + (5 - row);
                requestedGridKeys.Add(BuildGridKey(request.Key.Zone, request.Key.Meridian, globalX, globalY));
            }

            var requestedSectionKeys = new HashSet<string>(
                requests.Select(r => BuildSectionKeyId(r.Key)),
                StringComparer.OrdinalIgnoreCase);

            var townshipKeys = BuildContextTownshipKeys(requests).ToList();

            var processedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayer(database, tr, "L-QSEC");
                EnsureLayer(database, tr, "L-QSEC-BOX");
                SetLayerVisibility(database, tr, "L-QSEC-BOX", isOff: true, isPlottable: false);
                foreach (var townshipKey in townshipKeys)
                {
                    if (!TryParseTownshipKey(townshipKey, out var zone, out var meridian, out var range, out var township))
                    {
                        continue;
                    }

                    for (var section = 1; section <= 36; section++)
                    {
                        var key = new SectionKey(zone, section.ToString(CultureInfo.InvariantCulture), township, range, meridian);
                        var keyId = BuildSectionKeyId(key);
                        if (requestedSectionKeys.Contains(keyId) || !processedSections.Add(keyId))
                        {
                            continue;
                        }

                        if (!TryLoadSectionOutline(searchFolders, key, logger, out var outline))
                        {
                            continue;
                        }

                        using (var sectionPolyline = new Polyline(outline.Vertices.Count))
                        {
                            sectionPolyline.Closed = outline.Closed;
                            for (var i = 0; i < outline.Vertices.Count; i++)
                            {
                                sectionPolyline.AddVertexAt(i, outline.Vertices[i], 0, 0, 0);
                            }

                            if (!IntersectsAnyClipWindow(sectionPolyline.GeometricExtents))
                            {
                                continue;
                            }

                            if (requestedGridKeys.Count > 0 &&
                                TryParsePositiveToken(range, out var rangeNum) &&
                                TryParsePositiveToken(township, out var townshipNum) &&
                                TryGetAtsSectionGridPosition(section, out var row, out var col))
                            {
                                var globalX = (-rangeNum * 6) + col;
                                var globalY = (townshipNum * 6) + (5 - row);
                                var adjoiningRequested = false;
                                for (var dx = -1; dx <= 1 && !adjoiningRequested; dx++)
                                {
                                    for (var dy = -1; dy <= 1; dy++)
                                    {
                                        if (dx == 0 && dy == 0)
                                        {
                                            continue;
                                        }

                                        if (!requestedGridKeys.Contains(BuildGridKey(zone, meridian, globalX + dx, globalY + dy)))
                                        {
                                            continue;
                                        }

                                        adjoiningRequested = true;
                                        break;
                                    }
                                }

                                if (!adjoiningRequested)
                                {
                                    continue;
                                }
                            }

                            if (!TryGetQuarterAnchors(sectionPolyline, out var anchors))
                            {
                                anchors = GetFallbackAnchors(sectionPolyline);
                            }

                            var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                            var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                            if (!TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                                !TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) ||
                                !TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                                !TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
                            {
                                continue;
                            }

                            var secType = ResolveSectionType(key, "AUTO", inferredSecTypes);
                            var quarterSecTypes = ResolveQuarterSectionTypes(key, secType, inferredQuarterSecTypes);
                            EnsureSecTypeLayers(database, tr, secType, quarterSecTypes);

                            var helperSection = (Polyline)sectionPolyline.Clone();
                            helperSection.Layer = "L-QSEC-BOX";
                            helperSection.ColorIndex = 256;
                            var helperSectionId = modelSpace.AppendEntity(helperSection);
                            tr.AddNewlyCreatedDBObject(helperSection, true);
                            contextSectionOutlineHelperIds?.Add(helperSectionId);
                            contextRuleSectionInfos?.Add(new QuarterLabelInfo(
                                helperSectionId,
                                key,
                                QuarterSelection.SouthEast,
                                secType,
                                helperSectionId));

                            foreach (var id in DrawSectionBoundaryQuarterSegmentPolylines(
                                modelSpace,
                                tr,
                                nw,
                                ne,
                                sw,
                                se,
                                secType,
                                quarterSecTypes,
                                clipToWindow: null,
                                anchors.Top,
                                anchors.Right,
                                anchors.Bottom,
                                anchors.Left))
                            {
                                drawnIds.Add(id);
                            }

                            foreach (var id in DrawClippedQuarterDividerLinesForContext(
                                modelSpace,
                                tr,
                                anchors.Top,
                                anchors.Bottom,
                                anchors.Left,
                                anchors.Right,
                                Array.Empty<Extents3d>()))
                            {
                                drawnIds.Add(id);
                            }
                        }
                    }
                }

                tr.Commit();
            }

            if (drawnIds.Count > 0)
            {
                logger?.WriteLine($"Context sections drawn: {drawnIds.Count} piece(s) before final 100m trim.");
            }

            return drawnIds;
        }

        private static List<ObjectId> DrawClippedQuarterDividerLinesForContext(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point2d top,
            Point2d bottom,
            Point2d left,
            Point2d right,
            IReadOnlyList<Extents3d> clipWindows)
        {
            var ids = new List<ObjectId>();
            if (modelSpace == null || transaction == null)
            {
                return ids;
            }

            var segments = new[]
            {
                (A: top, B: bottom),
                (A: left, B: right)
            };

            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in segments)
            {
                if (clipWindows == null || clipWindows.Count == 0)
                {
                    if (!TryClipSegmentToWindow(seg.A, seg.B, (Extents3d?)null, out var a, out var b))
                    {
                        continue;
                    }

                    var line = new Line(new Point3d(a.X, a.Y, 0.0), new Point3d(b.X, b.Y, 0.0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var id = modelSpace.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                    ids.Add(id);
                    continue;
                }

                foreach (var win in clipWindows)
                {
                    if (!TryClipSegmentToWindow(seg.A, seg.B, win, out var a, out var b))
                    {
                        continue;
                    }

                    var p0 = a;
                    var p1 = b;
                    if (p1.X < p0.X || (Math.Abs(p1.X - p0.X) <= 1e-9 && p1.Y < p0.Y))
                    {
                        var tmp = p0;
                        p0 = p1;
                        p1 = tmp;
                    }

                    var key = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                        p0.X, p0.Y, p1.X, p1.Y);
                    if (!dedupe.Add(key))
                    {
                        continue;
                    }

                    var line = new Line(new Point3d(a.X, a.Y, 0.0), new Point3d(b.X, b.Y, 0.0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var id = modelSpace.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static bool TryGetBufferedQuarterWindow(
            Database database,
            IEnumerable<ObjectId> quarterIds,
            double buffer,
            out Extents3d window)
        {
            window = default;
            var windows = BuildBufferedQuarterWindows(database, quarterIds, buffer);
            if (windows.Count == 0)
            {
                return false;
            }

            window = UnionExtents3d(windows);
            return true;
        }

        private static List<Extents3d> BuildBufferedQuarterWindows(
            Database database,
            IEnumerable<ObjectId> quarterIds,
            double buffer)
        {
            var windows = new List<Extents3d>();
            if (database == null || quarterIds == null)
            {
                return windows;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var id in quarterIds.Distinct())
                {
                    if (id.IsNull || id.IsErased)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Polyline poly))
                    {
                        continue;
                    }

                    try
                    {
                        var ext = poly.GeometricExtents;
                        windows.Add(new Extents3d(
                            new Point3d(ext.MinPoint.X - buffer, ext.MinPoint.Y - buffer, 0.0),
                            new Point3d(ext.MaxPoint.X + buffer, ext.MaxPoint.Y + buffer, 0.0)));
                    }
                    catch
                    {
                    }
                }

                tr.Commit();
            }

            return windows;
        }

        private static Extents3d UnionExtents3d(IReadOnlyList<Extents3d> windows)
        {
            var union = windows[0];
            for (var i = 1; i < windows.Count; i++)
            {
                union.AddExtents(windows[i]);
            }

            return union;
        }

        private static bool DoExtentsOverlapOrTouch(Extents3d a, Extents3d b, double tolerance)
        {
            return a.MinPoint.X <= (b.MaxPoint.X + tolerance) &&
                   a.MaxPoint.X >= (b.MinPoint.X - tolerance) &&
                   a.MinPoint.Y <= (b.MaxPoint.Y + tolerance) &&
                   a.MaxPoint.Y >= (b.MinPoint.Y - tolerance);
        }

        private static List<Extents3d> MergeOverlappingClipWindows(IReadOnlyList<Extents3d> windows)
        {
            var result = new List<Extents3d>();
            if (windows == null || windows.Count == 0)
            {
                return result;
            }

            const double toleranceMeters = 0.01;

            static bool ContainsExtents(Extents3d outer, Extents3d inner, double tolerance)
            {
                return outer.MinPoint.X <= (inner.MinPoint.X + tolerance) &&
                       outer.MinPoint.Y <= (inner.MinPoint.Y + tolerance) &&
                       outer.MaxPoint.X >= (inner.MaxPoint.X - tolerance) &&
                       outer.MaxPoint.Y >= (inner.MaxPoint.Y - tolerance);
            }

            static bool ExtentsNearEqual(Extents3d a, Extents3d b, double tolerance)
            {
                return Math.Abs(a.MinPoint.X - b.MinPoint.X) <= tolerance &&
                       Math.Abs(a.MinPoint.Y - b.MinPoint.Y) <= tolerance &&
                       Math.Abs(a.MaxPoint.X - b.MaxPoint.X) <= tolerance &&
                       Math.Abs(a.MaxPoint.Y - b.MaxPoint.Y) <= tolerance;
            }

            foreach (var window in windows)
            {
                var skip = false;
                for (var i = result.Count - 1; i >= 0; i--)
                {
                    if (ContainsExtents(result[i], window, toleranceMeters) ||
                        ExtentsNearEqual(result[i], window, toleranceMeters))
                    {
                        skip = true;
                        break;
                    }

                    if (ContainsExtents(window, result[i], toleranceMeters))
                    {
                        result.RemoveAt(i);
                    }
                }

                if (!skip)
                {
                    result.Add(window);
                }
            }

            return result;
        }

        private static List<ObjectId> DrawRoadAllowanceGapOffsetLines(
            Database database,
            IReadOnlyList<string> searchFolders,
            IReadOnlyList<SectionRequest> requests,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyDictionary<string, string> inferredSecTypes,
            IReadOnlyDictionary<string, string> inferredQuarterSecTypes,
            Logger logger,
            IReadOnlyCollection<string>? activeSectionKeyIds = null)
        {
            var ids = new List<ObjectId>();
            if (database == null || searchFolders == null || requests == null || requestedQuarterIds == null)
            {
                return ids;
            }

            var rawClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(rawClipWindows);
            if (clipWindows.Count == 0)
            {
                return ids;
            }
            if (EnableRoadAllowanceDiagnostics)
            {
                logger?.WriteLine(RaDiagBuildTag);
                logger?.WriteLine($"RA-DIAG clip windows: raw={rawClipWindows.Count}, merged={clipWindows.Count}");
            }

            var selectedSectionKeyIds = activeSectionKeyIds != null && activeSectionKeyIds.Count > 0
                ? new HashSet<string>(activeSectionKeyIds.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    requests.Select(r => BuildSectionKeyId(r.Key)),
                    StringComparer.OrdinalIgnoreCase);

            var contextTownshipKeys = BuildContextTownshipKeys(requests);
            var secTypeLookup = inferredSecTypes ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var quarterTypeLookup = inferredQuarterSecTypes ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            static string ResolveEdgeSecTypeForRoadAllowance(
                string primary,
                string secondary,
                string fallback,
                bool allowSectionFallbackOnMixed)
            {
                var p = NormalizeSecType(primary);
                var s = NormalizeSecType(secondary);
                if (string.Equals(p, s, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }

                if (!allowSectionFallbackOnMixed)
                {
                    return "L-USEC";
                }

                return NormalizeSecType(fallback);
            }

            bool TryGetAtsSectionGridPosition(int section, out int row, out int col)
            {
                row = -1;
                col = -1;
                if (section < 1 || section > 36)
                {
                    return false;
                }

                // ATS/DLS section grid (6x6 snake pattern, north at top).
                // Alberta numbering starts at the southeast corner:
                // 6  5  4  3  2  1
                // 7  8  9 10 11 12
                // 18 17 16 15 14 13
                // 19 20 21 22 23 24
                // 30 29 28 27 26 25
                // 31 32 33 34 35 36
                var rows = new[]
                {
                    new[] { 31, 32, 33, 34, 35, 36 },
                    new[] { 30, 29, 28, 27, 26, 25 },
                    new[] { 19, 20, 21, 22, 23, 24 },
                    new[] { 18, 17, 16, 15, 14, 13 },
                    new[] { 7, 8, 9, 10, 11, 12 },
                    new[] { 6, 5, 4, 3, 2, 1 }
                };

                for (var r = 0; r < rows.Length; r++)
                {
                    for (var c = 0; c < rows[r].Length; c++)
                    {
                        if (rows[r][c] == section)
                        {
                            row = r;
                            col = c;
                            return true;
                        }
                    }
                }

                return false;
            }

            var offsetSpecs = new List<(Point2d A, Point2d B, string Tag, string Layer)>();
            var diagCandidates = 0;
            var diagEligible = 0;
            var diagMatched30 = 0;
            var diagMatched20 = 0;
            var geoms = new List<(
                string KeyId,
                string Label,
                Point2d SW,
                Point2d SE,
                Point2d NW,
                Point2d NE,
                Point2d LeftMid,
                Point2d RightMid,
                Point2d TopMid,
                Point2d BottomMid,
                Point2d Center,
                int Zone,
                string Meridian,
                int GlobalX,
                int GlobalY,
                string SouthEdgeLayer,
                string EastEdgeLayer,
                string NorthEdgeLayer,
                string WestEdgeLayer,
                string SwQuarterLayer,
                string SeQuarterLayer,
                string NeQuarterLayer,
                string NwQuarterLayer,
                bool IntersectsClipWindow)>();
            foreach (var townshipKey in contextTownshipKeys)
            {
                if (!TryParseTownshipKey(townshipKey, out var zone, out var meridian, out var range, out var township))
                {
                    continue;
                }

                for (var section = 1; section <= 36; section++)
                {
                    var sectionKey = new SectionKey(zone, section.ToString(CultureInfo.InvariantCulture), township, range, meridian);
                    if (!TryLoadSectionOutline(searchFolders, sectionKey, logger!, out var outline))
                    {
                        continue;
                    }

                    using (var poly = new Polyline(outline.Vertices.Count))
                    {
                        poly.Closed = outline.Closed;
                        for (var vi = 0; vi < outline.Vertices.Count; vi++)
                        {
                            poly.AddVertexAt(vi, outline.Vertices[vi], 0, 0, 0);
                        }

                        if (!TryGetQuarterAnchors(poly, out var anchors))
                        {
                            anchors = GetFallbackAnchors(poly);
                        }

                        var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                        var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                        if (!TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                            !TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.SouthEast, out var se) ||
                            !TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                            !TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne))
                        {
                            continue;
                        }

                        var center = new Point2d(
                            0.25 * (sw.X + se.X + nw.X + ne.X),
                            0.25 * (sw.Y + se.Y + nw.Y + ne.Y));
                        var rangeNum = 0;
                        var townshipNum = 0;
                        var hasRange = TryParsePositiveToken(range, out rangeNum);
                        var hasTownship = TryParsePositiveToken(township, out townshipNum);
                        var hasGrid = TryGetAtsSectionGridPosition(section, out var row, out var col);
                        // In ATS, range numbers increase westward, so invert range axis for adjacency math.
                        var globalX = (hasRange && hasGrid) ? ((-rangeNum * 6) + col) : int.MinValue;
                        // Township numbers increase northward; with row[0] at north, invert row to keep
                        // immediate north/south neighbors one grid step apart across township boundaries.
                        var globalY = (hasTownship && hasGrid) ? ((townshipNum * 6) + (5 - row)) : int.MinValue;
                        var resolvedSecType = ResolveSectionType(sectionKey, "AUTO", secTypeLookup);
                        var resolvedQuarterSecTypes = ResolveQuarterSectionTypes(sectionKey, resolvedSecType, quarterTypeLookup);
                        var swType = ResolveQuarterSecTypeForQuarter(resolvedQuarterSecTypes, QuarterSelection.SouthWest, resolvedSecType);
                        var seType = ResolveQuarterSecTypeForQuarter(resolvedQuarterSecTypes, QuarterSelection.SouthEast, resolvedSecType);
                        var neType = ResolveQuarterSecTypeForQuarter(resolvedQuarterSecTypes, QuarterSelection.NorthEast, resolvedSecType);
                        var nwType = ResolveQuarterSecTypeForQuarter(resolvedQuarterSecTypes, QuarterSelection.NorthWest, resolvedSecType);
                        var southEdgeType = ResolveEdgeSecTypeForRoadAllowance(swType, seType, resolvedSecType, allowSectionFallbackOnMixed: true);
                        var eastEdgeType = ResolveEdgeSecTypeForRoadAllowance(seType, neType, resolvedSecType, allowSectionFallbackOnMixed: false);
                        var northEdgeType = ResolveEdgeSecTypeForRoadAllowance(nwType, neType, resolvedSecType, allowSectionFallbackOnMixed: true);
                        var westEdgeType = ResolveEdgeSecTypeForRoadAllowance(swType, nwType, resolvedSecType, allowSectionFallbackOnMixed: false);
                        var ext = poly.GeometricExtents;
                        var intersectsClipWindow = false;
                        for (var wi = 0; wi < clipWindows.Count; wi++)
                        {
                            if (!GeometryUtils.ExtentsIntersect(ext, clipWindows[wi]))
                            {
                                continue;
                            }

                            intersectsClipWindow = true;
                            break;
                        }

                        geoms.Add((
                            BuildSectionKeyId(sectionKey),
                            BuildSectionDescriptor(sectionKey),
                            sw, se, nw, ne,
                            anchors.Left, anchors.Right, anchors.Top, anchors.Bottom,
                            center,
                            zone,
                            NormalizeNumberToken(meridian),
                            globalX,
                            globalY,
                            southEdgeType,
                            eastEdgeType,
                            northEdgeType,
                            westEdgeType,
                            swType,
                            seType,
                            neType,
                            nwType,
                            intersectsClipWindow));
                    }
                }
            }

            if (geoms.Count == 0)
            {
                return ids;
            }

            var localKeyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedGeoms = geoms
                .Where(g => selectedSectionKeyIds.Contains(g.KeyId))
                .ToList();
            if (selectedGeoms.Count > 0)
            {
                foreach (var g in geoms)
                {
                    foreach (var s in selectedGeoms)
                    {
                        if (g.Zone != s.Zone ||
                            !string.Equals(g.Meridian, s.Meridian, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var dx = g.Center.X - s.Center.X;
                        var dy = g.Center.Y - s.Center.Y;
                        var centerDistance = Math.Sqrt((dx * dx) + (dy * dy));
                        var spanG = Math.Max((g.SE - g.SW).Length, (g.NW - g.SW).Length);
                        var spanS = Math.Max((s.SE - s.SW).Length, (s.NW - s.SW).Length);
                        var neighborThreshold = Math.Max(spanG, spanS) * 1.8;
                        if (centerDistance <= neighborThreshold)
                        {
                            localKeyIds.Add(g.KeyId);
                            break;
                        }
                    }
                }
            }

            var clipWindowKeyIds = new HashSet<string>(
                geoms
                    .Where(g => g.IntersectsClipWindow)
                    .Select(g => g.KeyId),
                StringComparer.OrdinalIgnoreCase);

            var selectedOrLocalKeyIds = new HashSet<string>(selectedSectionKeyIds, StringComparer.OrdinalIgnoreCase);
            // When caller provides explicit active section keys (requested + adjoining built set),
            // keep RA generation strictly in that set for deterministic parity.
            if (activeSectionKeyIds == null || activeSectionKeyIds.Count == 0)
            {
                if (localKeyIds.Count > 0)
                {
                    selectedOrLocalKeyIds.UnionWith(localKeyIds);
                }

                selectedOrLocalKeyIds.UnionWith(clipWindowKeyIds);
            }
            if (EnableRoadAllowanceDiagnostics)
            {
                logger?.WriteLine($"RA-DIAG selection: requested={selectedSectionKeyIds.Count}, local={localKeyIds.Count}, clipWindow={clipWindowKeyIds.Count}, active={selectedOrLocalKeyIds.Count}");
            }

            var seenSpecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddSpec(Point2d s, Point2d e, string tag, string keyA, string keyB, bool verticalMode, string sourceLayer)
            {
                diagEligible++;
                var p0 = s;
                var p1 = e;
                if (p1.X < p0.X || (Math.Abs(p1.X - p0.X) < 1e-9 && p1.Y < p0.Y))
                {
                    var tmp = p0;
                    p0 = p1;
                    p1 = tmp;
                }

                var first = string.Compare(keyA, keyB, StringComparison.OrdinalIgnoreCase) <= 0 ? keyA : keyB;
                var second = string.Equals(first, keyA, StringComparison.OrdinalIgnoreCase) ? keyB : keyA;
                var dedupeKey = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3:0.###}|{4:0.###}|{5:0.###}|{6:0.###}",
                    first,
                    second,
                    verticalMode ? "V" : "H",
                    p0.X, p0.Y, p1.X, p1.Y);
                if (!seenSpecs.Add(dedupeKey))
                    return;

                offsetSpecs.Add((s, e, tag, NormalizeSecType(sourceLayer)));
                if (EnableRoadAllowanceDiagnostics)
                    logger?.WriteLine($"RA-DIAG ADD {tag}: layer={NormalizeSecType(sourceLayer)} A=({s.X:0.###},{s.Y:0.###}) B=({e.X:0.###},{e.Y:0.###})");
            }

            void TryAddFromPair(
                string baseKey,
                string baseLabel,
                Point2d baseStart,
                Point2d baseMid,
                Point2d baseEnd,
                string baseLayerP1,
                string baseLayerP2,
                string otherKey,
                string otherLabel,
                Point2d otherStart,
                Point2d otherMid,
                Point2d otherEnd,
                string otherLayerP1,
                string otherLayerP2,
                bool verticalMode)
            {
                double? gapP1 = null;
                double? gapP2 = null;

                void TryAddQuarterOffsetSegment(
                    Point2d b0,
                    Point2d b1,
                    Point2d o0,
                    Point2d o1,
                    string part,
                    string sourceLayer,
                    out double? gapOut)
                {
                    gapOut = null;
                    var baseDir = b1 - b0;
                    var otherDir = o1 - o0;
                    var baseLen = baseDir.Length;
                    var otherLen = otherDir.Length;
                    if (baseLen <= 1e-6 || otherLen <= 1e-6)
                    {
                        return;
                    }

                    var baseU = baseDir / baseLen;
                    var otherU = otherDir / otherLen;
                    if (Math.Abs(baseU.DotProduct(otherU)) < 0.99)
                    {
                        return;
                    }

                    if (baseU.DotProduct(otherU) < 0.0)
                    {
                        var tmp = o0;
                        o0 = o1;
                        o1 = tmp;
                        otherDir = o1 - o0;
                        otherLen = otherDir.Length;
                        if (otherLen <= 1e-6)
                        {
                            return;
                        }
                    }

                    var tBase0 = 0.0;
                    var tBase1 = baseLen;
                    var tOther0 = (o0 - b0).DotProduct(baseU);
                    var tOther1 = (o1 - b0).DotProduct(baseU);
                    var overlapMin = Math.Max(Math.Min(tBase0, tBase1), Math.Min(tOther0, tOther1));
                    var overlapMax = Math.Min(Math.Max(tBase0, tBase1), Math.Max(tOther0, tOther1));
                    var rawOverlapMin = overlapMin;
                    var rawOverlapMax = overlapMax;
                    const double endpointSnapToleranceMeters = 1.0;
                    if (overlapMin > 0.0 && overlapMin < endpointSnapToleranceMeters)
                    {
                        overlapMin = 0.0;
                    }

                    if (overlapMax < baseLen && (baseLen - overlapMax) < endpointSnapToleranceMeters)
                    {
                        overlapMax = baseLen;
                    }

                    var overlapLength = overlapMax - overlapMin;
                    var minEdgeLength = Math.Min(baseLen, otherLen);
                    if (overlapLength < Math.Max(100.0, minEdgeLength * 0.75))
                    {
                        return;
                    }

                    if (EnableRoadAllowanceDiagnostics)
                    {
                        logger?.WriteLine(
                            $"RA-DIAG overlap {(verticalMode ? "V" : "H")} {baseLabel}/{otherLabel} {part}: " +
                            $"baseLen={baseLen:0.###}, raw=[{rawOverlapMin:0.###},{rawOverlapMax:0.###}], " +
                            $"snapped=[{overlapMin:0.###},{overlapMax:0.###}], overlap={overlapLength:0.###}");
                    }

                    var clippedBaseStart = b0 + (baseU * overlapMin);
                    var clippedBaseEnd = b0 + (baseU * overlapMax);
                    var otherLen2 = otherDir.DotProduct(otherDir);
                    if (otherLen2 <= 1e-9)
                    {
                        return;
                    }

                    var sample = b0 + (baseU * (overlapMin + (0.5 * (overlapMax - overlapMin))));
                    var proj = (sample - o0).DotProduct(otherDir) / otherLen2;
                    proj = Math.Max(0.0, Math.Min(1.0, proj));
                    var otherSample = o0 + (otherDir * proj);

                    var normal = new Vector2d(-baseU.Y, baseU.X);
                    if ((otherSample - sample).DotProduct(normal) < 0.0)
                    {
                        normal = -normal;
                    }

                    var gap = Math.Abs((otherSample - sample).DotProduct(normal));
                    gapOut = gap;
                    diagCandidates++;
                    if (Math.Abs(gap - RoadAllowanceUsecWidthMeters) <= 0.50) diagMatched30++;
                    if (Math.Abs(gap - RoadAllowanceSecWidthMeters) <= 0.50) diagMatched20++;

                    if (Math.Abs(gap - RoadAllowanceSecWidthMeters) <= RoadAllowanceWidthToleranceMeters)
                    {
                        return;
                    }

                    if (Math.Abs(gap - RoadAllowanceUsecWidthMeters) > RoadAllowanceGapOffsetToleranceMeters)
                    {
                        return;
                    }

                    var offsetStart = clippedBaseStart + (normal * RoadAllowanceSecWidthMeters);
                    var offsetEnd = clippedBaseEnd + (normal * RoadAllowanceSecWidthMeters);
                    var tag = $"{(verticalMode ? "V" : "H")} {baseLabel}/{otherLabel} {part} gap={gap:0.###}";
                    AddSpec(offsetStart, offsetEnd, tag, baseKey, otherKey, verticalMode, sourceLayer);
                }

                // Per-half layer attribution avoids one quarter forcing the sibling quarter.
                TryAddQuarterOffsetSegment(baseStart, baseMid, otherStart, otherMid, "P1", baseLayerP1, out gapP1);
                TryAddQuarterOffsetSegment(baseMid, baseEnd, otherMid, otherEnd, "P2", baseLayerP2, out gapP2);

                if (EnableRoadAllowanceDiagnostics)
                {
                    logger?.WriteLine(
                        $"RA-DIAG {(verticalMode ? "V" : "H")} pair {baseLabel}/{otherLabel}: " +
                        $"gapP1={(gapP1.HasValue ? gapP1.Value.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")} " +
                        $"gapP2={(gapP2.HasValue ? gapP2.Value.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")} " +
                        $"layersP1={NormalizeSecType(baseLayerP1)}/{NormalizeSecType(otherLayerP1)} " +
                        $"layersP2={NormalizeSecType(baseLayerP2)}/{NormalizeSecType(otherLayerP2)}");
                }
            }

            string BuildGridKey(int zone, string meridian, int globalX, int globalY)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3}",
                    zone,
                    meridian ?? string.Empty,
                    globalX,
                    globalY);
            }

            var geomIndexByGrid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < geoms.Count; i++)
            {
                var g = geoms[i];
                if (g.GlobalX == int.MinValue || g.GlobalY == int.MinValue)
                {
                    continue;
                }

                var key = BuildGridKey(g.Zone, g.Meridian, g.GlobalX, g.GlobalY);
                if (!geomIndexByGrid.ContainsKey(key))
                {
                    geomIndexByGrid[key] = i;
                }
            }

            bool TryGetNeighborIndex(int zone, string meridian, int globalX, int globalY, int dx, int dy, out int index)
            {
                var key = BuildGridKey(zone, meridian, globalX + dx, globalY + dy);
                return geomIndexByGrid.TryGetValue(key, out index);
            }

            bool IsNeighborPairEligible(
                (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d LeftMid, Point2d RightMid, Point2d TopMid, Point2d BottomMid, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY, string SouthEdgeLayer, string EastEdgeLayer, string NorthEdgeLayer, string WestEdgeLayer, string SwQuarterLayer, string SeQuarterLayer, string NeQuarterLayer, string NwQuarterLayer, bool IntersectsClipWindow) a,
                (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d LeftMid, Point2d RightMid, Point2d TopMid, Point2d BottomMid, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY, string SouthEdgeLayer, string EastEdgeLayer, string NorthEdgeLayer, string WestEdgeLayer, string SwQuarterLayer, string SeQuarterLayer, string NeQuarterLayer, string NwQuarterLayer, bool IntersectsClipWindow) b)
            {
                if (!selectedOrLocalKeyIds.Contains(a.KeyId) &&
                    !selectedOrLocalKeyIds.Contains(b.KeyId))
                {
                    return false;
                }

                var centerDx = b.Center.X - a.Center.X;
                var centerDy = b.Center.Y - a.Center.Y;
                var centerDistance = Math.Sqrt((centerDx * centerDx) + (centerDy * centerDy));
                var spanA = Math.Max((a.SE - a.SW).Length, (a.NW - a.SW).Length);
                var spanB = Math.Max((b.SE - b.SW).Length, (b.NW - b.SW).Length);
                return centerDistance <= (Math.Max(spanA, spanB) * 1.8);
            }

            for (var i = 0; i < geoms.Count; i++)
            {
                var a = geoms[i];
                if (a.GlobalX == int.MinValue || a.GlobalY == int.MinValue)
                {
                    continue;
                }

                // Immediate east/west neighbor (grid +1 X) only.
                if (TryGetNeighborIndex(a.Zone, a.Meridian, a.GlobalX, a.GlobalY, 1, 0, out var eastNeighborIndex))
                {
                    var b = geoms[eastNeighborIndex];
                    if (IsNeighborPairEligible(a, b))
                    {
                        var aIsWest = a.Center.X <= b.Center.X;
                        var west = aIsWest ? a : b;
                        var east = aIsWest ? b : a;
                        TryAddFromPair(
                            west.KeyId, west.Label, west.SE, west.RightMid, west.NE,
                            west.SeQuarterLayer,
                            west.NeQuarterLayer,
                            east.KeyId, east.Label, east.SW, east.LeftMid, east.NW,
                            east.SwQuarterLayer,
                            east.NwQuarterLayer,
                            verticalMode: true);
                    }
                }

                // Immediate north/south neighbor (grid +1 Y) only.
                if (TryGetNeighborIndex(a.Zone, a.Meridian, a.GlobalX, a.GlobalY, 0, 1, out var northNeighborIndex))
                {
                    var b = geoms[northNeighborIndex];
                    if (IsNeighborPairEligible(a, b))
                    {
                        var aIsSouth = a.Center.Y <= b.Center.Y;
                        var south = aIsSouth ? a : b;
                        var north = aIsSouth ? b : a;
                        TryAddFromPair(
                            south.KeyId, south.Label, south.NW, south.TopMid, south.NE,
                            south.NwQuarterLayer,
                            south.NeQuarterLayer,
                            north.KeyId, north.Label, north.SW, north.BottomMid, north.SE,
                            north.SwQuarterLayer,
                            north.SeQuarterLayer,
                            verticalMode: false);
                    }
                }
            }

            if (EnableRoadAllowanceDiagnostics)
            {
                logger?.WriteLine($"RA-DIAG summary: candidates={diagCandidates}, eligible={diagEligible}, near30.16={diagMatched30}, near20.11={diagMatched20}, addPreClip={offsetSpecs.Count}");
            }

            if (offsetSpecs.Count == 0)
            {
                return ids;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var drawnSegmentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var clippedSegments = new List<(Point2d A, Point2d B, string Tag, string Layer)>();

                foreach (var seg in offsetSpecs)
                {
                    var intersectsWindow = false;
                    foreach (var clipWindow in clipWindows)
                    {
                        if (!TryClipSegmentToWindow(seg.A, seg.B, clipWindow, out _, out _))
                        {
                            continue;
                        }

                        intersectsWindow = true;
                        break;
                    }

                    if (!intersectsWindow)
                    {
                        if (EnableRoadAllowanceDiagnostics)
                        {
                            logger?.WriteLine($"RA-DIAG CLIP-OUT {seg.Tag}");
                        }

                        continue;
                    }

                    var p0 = seg.A;
                    var p1 = seg.B;
                    if (p1.X < p0.X || (Math.Abs(p1.X - p0.X) <= 1e-9 && p1.Y < p0.Y))
                    {
                        var tmp = p0;
                        p0 = p1;
                        p1 = tmp;
                    }

                    var key = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.###},{1:0.###},{2:0.###},{3:0.###}|{4}",
                        p0.X, p0.Y, p1.X, p1.Y,
                        NormalizeSecType(seg.Layer));
                    if (!drawnSegmentKeys.Add(key))
                    {
                        continue;
                    }

                    clippedSegments.Add((seg.A, seg.B, seg.Tag, seg.Layer));
                    if (EnableRoadAllowanceDiagnostics)
                    {
                        logger?.WriteLine($"RA-DIAG DRAW {seg.Tag}: layer={seg.Layer} A=({seg.A.X:0.###},{seg.A.Y:0.###}) B=({seg.B.X:0.###},{seg.B.Y:0.###})");
                    }
                }

                var mergedSegments = MergeCollinearRoadAllowanceSegments(clippedSegments);
                var alignedSegments = AlignRoadAllowanceSegmentEndpointsToSectionBoundaries(
                    tr,
                    modelSpace,
                    clipWindows,
                    mergedSegments,
                    logger);
                if (EnableRoadAllowanceDiagnostics)
                {
                    logger?.WriteLine($"RA-DIAG merge: raw={clippedSegments.Count}, merged={mergedSegments.Count}, aligned={alignedSegments.Count}");
                }

                var layersToEnsure = alignedSegments
                    .Select(s => NormalizeSecType(s.Layer))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (layersToEnsure.Count == 0)
                {
                    layersToEnsure.Add("L-USEC");
                }

                foreach (var layer in layersToEnsure)
                {
                    EnsureLayer(database, tr, layer);
                }

                foreach (var seg in alignedSegments)
                {
                    var poly = new Polyline(2)
                    {
                        Layer = NormalizeSecType(seg.Layer),
                        ColorIndex = 256
                    };
                    poly.AddVertexAt(0, seg.A, 0, 0, 0);
                    poly.AddVertexAt(1, seg.B, 0, 0, 0);
                    var id = modelSpace.AppendEntity(poly);
                    tr.AddNewlyCreatedDBObject(poly, true);
                    ids.Add(id);
                }

                tr.Commit();
            }

            logger?.WriteLine($"Road allowance offset lines drawn: {ids.Count}");
            return ids;
        }

        private static List<(Point2d A, Point2d B, string Tag, string Layer)> AlignRoadAllowanceSegmentEndpointsToSectionBoundaries(
            Transaction transaction,
            BlockTableRecord modelSpace,
            IReadOnlyList<Extents3d> clipWindows,
            List<(Point2d A, Point2d B, string Tag, string Layer)> segments,
            Logger? logger)
        {
            if (transaction == null || modelSpace == null || segments == null || segments.Count == 0)
            {
                return segments ?? new List<(Point2d A, Point2d B, string Tag, string Layer)>();
            }

            bool IsPointInAnyWindow(Point2d point)
            {
                if (clipWindows == null || clipWindows.Count == 0)
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (point.X >= w.MinPoint.X && point.X <= w.MaxPoint.X &&
                        point.Y >= w.MinPoint.Y && point.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (clipWindows == null || clipWindows.Count == 0)
                {
                    return true;
                }

                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            var boundarySegments = new List<(Point2d A, Point2d B)>();
            foreach (ObjectId id in modelSpace)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                {
                    continue;
                }

                if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryReadOpenSegment(ent, out var a, out var b))
                {
                    continue;
                }

                if (!DoesSegmentIntersectAnyWindow(a, b))
                {
                    continue;
                }

                boundarySegments.Add((a, b));
            }

            if (boundarySegments.Count == 0)
            {
                return segments;
            }

            bool TryClampSegmentEndsToBoundaryIntersections(
                Point2d start,
                Point2d end,
                out Point2d clampedStart,
                out Point2d clampedEnd,
                out double startShift,
                out double endShift)
            {
                clampedStart = start;
                clampedEnd = end;
                startShift = 0.0;
                endShift = 0.0;

                var axis = end - start;
                var length = axis.Length;
                if (length <= 1e-6)
                {
                    return false;
                }

                var axisUnit = axis / length;
                var center = Midpoint(start, end);
                var tMin = -0.5 * length;
                var tMax = 0.5 * length;
                const double minT = 1e-3;
                const double maxShrinkMeters = 8.0;
                const double maxExtendMeters = 0.75;
                double? bestNeg = null;
                double? bestPos = null;

                foreach (var candidate in boundarySegments)
                {
                    if (!TryIntersectInfiniteLineWithSegment(center, axisUnit, candidate.A, candidate.B, out var t))
                    {
                        continue;
                    }

                    if (t < -minT)
                    {
                        if (!bestNeg.HasValue || t > bestNeg.Value)
                        {
                            bestNeg = t;
                        }
                    }
                    else if (t > minT)
                    {
                        if (!bestPos.HasValue || t < bestPos.Value)
                        {
                            bestPos = t;
                        }
                    }
                }

                var newMin = tMin;
                var newMax = tMax;
                var changed = false;

                if (bestNeg.HasValue)
                {
                    var delta = bestNeg.Value - tMin;
                    var canAdjust = delta >= 0.0
                        ? delta <= maxShrinkMeters
                        : (-delta) <= maxExtendMeters;
                    if (canAdjust)
                    {
                        newMin = bestNeg.Value;
                        startShift = Math.Abs(delta);
                        changed = true;
                    }
                }

                if (bestPos.HasValue)
                {
                    var delta = bestPos.Value - tMax;
                    var canAdjust = delta <= 0.0
                        ? (-delta) <= maxShrinkMeters
                        : delta <= maxExtendMeters;
                    if (canAdjust)
                    {
                        newMax = bestPos.Value;
                        endShift = Math.Abs(delta);
                        changed = true;
                    }
                }

                if (!changed || (newMax - newMin) <= minT)
                {
                    return false;
                }

                clampedStart = center + (axisUnit * newMin);
                clampedEnd = center + (axisUnit * newMax);
                return clampedStart.GetDistanceTo(clampedEnd) > 1e-4;
            }

            var result = new List<(Point2d A, Point2d B, string Tag, string Layer)>(segments.Count);
            foreach (var seg in segments)
            {
                var a = seg.A;
                var b = seg.B;
                var clamped = TryClampSegmentEndsToBoundaryIntersections(
                    seg.A,
                    seg.B,
                    out var aClamp,
                    out var bClamp,
                    out var aShift,
                    out var bShift);
                if (clamped)
                {
                    a = aClamp;
                    b = bClamp;
                }

                if (a.GetDistanceTo(b) <= 1e-4)
                {
                    result.Add(seg);
                    continue;
                }

                if (EnableRoadAllowanceDiagnostics && clamped)
                {
                    logger?.WriteLine(
                        $"RA-DIAG END-CLAMP {seg.Tag}: " +
                        $"Ashift={aShift:0.###} Bshift={bShift:0.###} " +
                        $"A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###})");
                }

                result.Add((a, b, seg.Tag, seg.Layer));
            }

            return result;
        }

        private static List<(Point2d A, Point2d B, string Tag, string Layer)> MergeCollinearRoadAllowanceSegments(List<(Point2d A, Point2d B, string Tag, string Layer)> input)
        {
            var merged = input == null
                ? new List<(Point2d A, Point2d B, string Tag, string Layer)>()
                : new List<(Point2d A, Point2d B, string Tag, string Layer)>(input);
            if (merged.Count <= 1)
            {
                return merged;
            }

            bool changed;
            do
            {
                changed = false;
                for (var i = 0; i < merged.Count; i++)
                {
                    for (var j = i + 1; j < merged.Count; j++)
                    {
                        // Keep merges scoped to the same originating RA pair/part.
                        // Cross-tag merges can chain 20.11 segments across sections.
                        if (!string.Equals(merged[i].Tag, merged[j].Tag, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!string.Equals(merged[i].Layer, merged[j].Layer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryMergeCollinearSegments(merged[i].A, merged[i].B, merged[j].A, merged[j].B, out var outA, out var outB))
                        {
                            continue;
                        }

                        merged[i] = (outA, outB, merged[i].Tag, merged[i].Layer);
                        merged.RemoveAt(j);
                        changed = true;
                        break;
                    }

                    if (changed)
                    {
                        break;
                    }
                }
            } while (changed);

            return merged;
        }

        private static bool TryMergeCollinearSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d mergedA, out Point2d mergedB)
        {
            mergedA = a0;
            mergedB = a1;

            const double angleTol = 0.0015;
            const double distanceTol = 0.20;
            const double gapTol = 0.75;

            var ua = a1 - a0;
            var ub = b1 - b0;
            var la = ua.Length;
            var lb = ub.Length;
            if (la <= 1e-6 || lb <= 1e-6)
            {
                return false;
            }

            var cross = Math.Abs((ua.X * ub.Y) - (ua.Y * ub.X)) / (la * lb);
            if (cross > angleTol)
            {
                return false;
            }

            if (DistancePointToInfiniteLine(b0, a0, a1) > distanceTol ||
                DistancePointToInfiniteLine(b1, a0, a1) > distanceTol)
            {
                return false;
            }

            var axis = ua / la;
            var tA0 = 0.0;
            var tA1 = la;
            var tB0 = (b0 - a0).DotProduct(axis);
            var tB1 = (b1 - a0).DotProduct(axis);

            var aMin = Math.Min(tA0, tA1);
            var aMax = Math.Max(tA0, tA1);
            var bMin = Math.Min(tB0, tB1);
            var bMax = Math.Max(tB0, tB1);

            if (Math.Min(aMax, bMax) < (Math.Max(aMin, bMin) - gapTol))
            {
                return false;
            }

            var tMin = Math.Min(aMin, bMin);
            var tMax = Math.Max(aMax, bMax);
            mergedA = a0 + (axis * tMin);
            mergedB = a0 + (axis * tMax);
            return mergedA.GetDistanceTo(mergedB) > 1e-4;
        }

        private static void CleanupContextSectionOverlaps(Database database, IReadOnlyCollection<ObjectId> contextSectionIds, Logger? logger)
        {
            if (database == null || contextSectionIds == null || contextSectionIds.Count == 0)
            {
                return;
            }

            var contextSet = new HashSet<ObjectId>(contextSectionIds);
            var contextSegments = new List<(ObjectId Id, Point2d A, Point2d B, double Length)>();
            var otherSegments = new List<(ObjectId Id, Point2d A, Point2d B, double Length)>();
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Polyline pl))
                    {
                        continue;
                    }

                    if (!string.Equals(pl.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(pl.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (pl.Closed || pl.NumberOfVertices != 2)
                    {
                        continue;
                    }

                    var a = pl.GetPoint2dAt(0);
                    var b = pl.GetPoint2dAt(1);
                    var len = a.GetDistanceTo(b);
                    if (len <= 1e-4)
                    {
                        continue;
                    }

                    if (contextSet.Contains(id))
                    {
                        contextSegments.Add((id, a, b, len));
                    }
                    else
                    {
                        otherSegments.Add((id, a, b, len));
                    }
                }

                var toErase = new HashSet<ObjectId>();
                // Remove context segments that duplicate/overlap any existing section linework.
                foreach (var c in contextSegments)
                {
                    foreach (var o in otherSegments)
                    {
                        if (!AreSegmentsDuplicateOrCollinearOverlap(c.A, c.B, o.A, o.B))
                        {
                            continue;
                        }

                        toErase.Add(c.Id);
                        break;
                    }
                }

                // De-dupe within context fragments themselves: keep longer one.
                for (var i = 0; i < contextSegments.Count; i++)
                {
                    var s1 = contextSegments[i];
                    if (toErase.Contains(s1.Id))
                    {
                        continue;
                    }

                    for (var j = i + 1; j < contextSegments.Count; j++)
                    {
                        var s2 = contextSegments[j];
                        if (toErase.Contains(s2.Id))
                        {
                            continue;
                        }

                        if (!AreSegmentsDuplicateOrCollinearOverlap(s1.A, s1.B, s2.A, s2.B))
                        {
                            continue;
                        }

                        var eraseId = s1.Length < s2.Length ? s1.Id : s2.Id;
                        if (Math.Abs(s1.Length - s2.Length) <= 0.01)
                        {
                            eraseId = s1.Id.Handle.Value > s2.Id.Handle.Value ? s1.Id : s2.Id;
                        }

                        toErase.Add(eraseId);
                    }
                }

                var erased = 0;
                foreach (var id in toErase)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Erase();
                    erased++;
                }

                tr.Commit();
                if (erased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {erased} overlapping context L-SEC/L-USEC segment(s).");
                }
            }
        }

        private static void CleanupGeneratedRoadAllowanceOverlaps(Database database, IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds, Logger? logger)
        {
            if (database == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds);
            var generatedSegments = new List<(ObjectId Id, Point2d A, Point2d B, double Length)>();
            var existingSegments = new List<(ObjectId Id, Point2d A, Point2d B, string Layer, double Length)>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
                {
                    a = default;
                    b = default;
                    if (ent == null)
                    {
                        return false;
                    }

                    if (ent is Polyline pl)
                    {
                        if (pl.Closed || pl.NumberOfVertices != 2)
                        {
                            return false;
                        }

                        a = pl.GetPoint2dAt(0);
                        b = pl.GetPoint2dAt(1);
                        return a.GetDistanceTo(b) > 1e-4;
                    }

                    if (ent is Line ln)
                    {
                        a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        return a.GetDistanceTo(b) > 1e-4;
                    }

                    return false;
                }

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !IsUsecLayer(ent.Layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        generatedSegments.Add((id, a, b, a.GetDistanceTo(b)));
                    }
                    else
                    {
                        existingSegments.Add((id, a, b, ent.Layer ?? string.Empty, a.GetDistanceTo(b)));
                    }
                }

                const double containTol = 0.40;
                const double lengthDeltaTol = 0.35;
                bool IsSegmentContained(Point2d innerA, Point2d innerB, Point2d outerA, Point2d outerB)
                {
                    return DistancePointToSegment(innerA, outerA, outerB) <= containTol &&
                           DistancePointToSegment(innerB, outerA, outerB) <= containTol;
                }

                var toEraseGenerated = new HashSet<ObjectId>();
                foreach (var g in generatedSegments)
                {
                    if (toEraseGenerated.Contains(g.Id))
                    {
                        continue;
                    }

                    foreach (var e in existingSegments)
                    {
                        if (!AreSegmentsDuplicateOrCollinearOverlap(g.A, g.B, e.A, e.B))
                        {
                            continue;
                        }

                        var existingInsideGenerated = IsSegmentContained(e.A, e.B, g.A, g.B);
                        var generatedInsideExisting = IsSegmentContained(g.A, g.B, e.A, e.B);
                        if (existingInsideGenerated && (g.Length - e.Length) > lengthDeltaTol)
                        {
                            // Preserve existing section/context fabric; trim/cleanup generated instead.
                            toEraseGenerated.Add(g.Id);
                            break;
                        }

                        if (generatedInsideExisting && (e.Length - g.Length) > lengthDeltaTol)
                        {
                            // Existing full-length boundary already present; drop redundant generated.
                            toEraseGenerated.Add(g.Id);
                            break;
                        }

                        // Near-equal overlap:
                        // keep existing fabric (L-SEC and L-USEC families) as authoritative.
                        toEraseGenerated.Add(g.Id);
                        break;
                    }
                }

                var erasedGenerated = 0;
                foreach (var id in toEraseGenerated)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Erase();
                    erasedGenerated++;
                }

                tr.Commit();
                if (erasedGenerated > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: overlap resolve erased {erasedGenerated} generated RA segment(s) (existing boundaries preserved).");
                }
            }
        }

        private static void TrimContextSectionsToBufferedWindows(
            Database database,
            ICollection<ObjectId> contextSectionIds,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || contextSectionIds == null || contextSectionIds.Count == 0 || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);

                bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
                {
                    a = default;
                    b = default;
                    if (ent == null)
                    {
                        return false;
                    }

                    if (ent is Line ln)
                    {
                        a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        return a.GetDistanceTo(b) > 1e-4;
                    }

                    if (ent is Polyline pl)
                    {
                        if (pl.Closed || pl.NumberOfVertices != 2)
                        {
                            return false;
                        }

                        a = pl.GetPoint2dAt(0);
                        b = pl.GetPoint2dAt(1);
                        return a.GetDistanceTo(b) > 1e-4;
                    }

                    return false;
                }

                bool TryWriteOpenSegment(Entity ent, Point2d a, Point2d b)
                {
                    if (ent is Line ln)
                    {
                        ln.StartPoint = new Point3d(a.X, a.Y, ln.StartPoint.Z);
                        ln.EndPoint = new Point3d(b.X, b.Y, ln.EndPoint.Z);
                        return true;
                    }

                    if (ent is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        pl.SetPointAt(0, a);
                        pl.SetPointAt(1, b);
                        return true;
                    }

                    return false;
                }

                var trimmed = 0;
                var erased = 0;
                const double endpointMoveTol = 0.05;
                foreach (var id in contextSectionIds.Where(x => !x.IsNull).Distinct().ToList())
                {
                    if (id.IsNull || id.IsErased)
                    {
                        if (!contextSectionIds.IsReadOnly)
                        {
                            contextSectionIds.Remove(id);
                        }
                        continue;
                    }

                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        if (!contextSectionIds.IsReadOnly)
                        {
                            contextSectionIds.Remove(id);
                        }
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        if (!contextSectionIds.IsReadOnly)
                        {
                            contextSectionIds.Remove(id);
                        }
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var clipped = new List<(Point2d A, Point2d B)>();
                    var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var wi = 0; wi < clipWindows.Count; wi++)
                    {
                        if (!TryClipSegmentToWindow(a, b, clipWindows[wi], out var c0, out var c1))
                        {
                            continue;
                        }

                        var p0 = c0;
                        var p1 = c1;
                        if (p1.X < p0.X || (Math.Abs(p1.X - p0.X) <= 1e-9 && p1.Y < p0.Y))
                        {
                            var tmp = p0;
                            p0 = p1;
                            p1 = tmp;
                        }

                        var key = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:0.###}|{1:0.###}|{2:0.###}|{3:0.###}",
                            p0.X, p0.Y, p1.X, p1.Y);
                        if (!dedupe.Add(key))
                        {
                            continue;
                        }

                        clipped.Add((c0, c1));
                    }

                    bool merged;
                    do
                    {
                        merged = false;
                        for (var i = 0; i < clipped.Count; i++)
                        {
                            for (var j = i + 1; j < clipped.Count; j++)
                            {
                                if (!TryMergeCollinearSegments(clipped[i].A, clipped[i].B, clipped[j].A, clipped[j].B, out var mA, out var mB))
                                {
                                    continue;
                                }

                                clipped[i] = (mA, mB);
                                clipped.RemoveAt(j);
                                merged = true;
                                break;
                            }

                            if (merged)
                            {
                                break;
                            }
                        }
                    } while (merged);

                    if (clipped.Count == 0)
                    {
                        ent.Erase();
                        if (!contextSectionIds.IsReadOnly)
                        {
                            contextSectionIds.Remove(id);
                        }
                        erased++;
                        continue;
                    }

                    var primary = clipped
                        .OrderByDescending(s => s.A.GetDistanceTo(s.B))
                        .First();
                    var changed = primary.A.GetDistanceTo(a) > endpointMoveTol ||
                                  primary.B.GetDistanceTo(b) > endpointMoveTol;
                    if (changed && TryWriteOpenSegment(ent, primary.A, primary.B))
                    {
                        trimmed++;
                    }
                }

                tr.Commit();
                if (trimmed > 0 || erased > 0)
                {
                    logger?.WriteLine($"Cleanup: final 100m trim adjusted {trimmed} context segment(s), erased {erased} outside segment(s).");
                }
            }
        }

        private static void ConnectDanglingUsecZeroTwentyEndpoints(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices != 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable is Line ln)
                {
                    var old = moveStart
                        ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                        : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        ln.StartPoint = new Point3d(target.X, target.Y, ln.StartPoint.Z);
                    }
                    else
                    {
                        ln.EndPoint = new Point3d(target.X, target.Y, ln.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                {
                    var index = moveStart ? 0 : 1;
                    var old = pl.GetPoint2dAt(index);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(index, target);
                    return true;
                }

                return false;
            }

            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection)
            {
                intersection = default;
                var da = a1 - a0;
                var db = b1 - b0;
                var denom = Cross2d(da, db);
                if (Math.Abs(denom) <= 1e-9)
                {
                    return false;
                }

                var diff = b0 - a0;
                var t = Cross2d(diff, db) / denom;
                intersection = a0 + (da * t);
                return true;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool IsUsecZeroOrTwentyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
            }

            bool IsSectionLineLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
            }

            bool IsRoadLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var allRoadSegments = new List<(ObjectId Id, Point2d A, Point2d B, string Layer)>();
                var trackedSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool MovableSource, bool IsBaseUsecTarget, string Layer)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsRoadLayer(layer))
                    {
                        allRoadSegments.Add((id, a, b, layer));
                    }

                    var isMovableSource = IsUsecZeroOrTwentyLayer(layer);
                    var isBaseUsecTarget =
                        string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    // Square-closure mode for interior boundaries:
                    // only 0/20 lines are movable and valid targets (0->0, 20->20).
                    var isConnectTarget = isMovableSource;
                    if (!isConnectTarget)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    trackedSegments.Add((id, a, b, horizontal, vertical, isMovableSource, isBaseUsecTarget, layer));
                }

                if (trackedSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointHitTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double endpointSnapTol = 1.0;
                const double minExtend = 0.05;
                const double maxExtend = 45.0;
                const double targetOnSegmentTol = 0.65;
                const double targetOnSegmentTolRelaxed = 1.50;
                const double apparentEndpointGapTol = 24.0;
                const double outerBoundaryTol = 0.40;
                var scannedEndpoints = 0;
                var skippedOnOuterBoundary = 0;
                var alreadyConnected = 0;
                var noThirtyContext = 0;
                var noTarget = 0;
                var moved = 0;
                var sideRejected = 0;
                var sideRuleFallbackUsed = 0;
                var baseTargetFallbackUsed = 0;
                var apparentFallbackUsed = 0;
                var nonSectionSourceSkipped = 0;
                var nonSectionTargetRejected = 0;
                var crossLayerFallbackUsed = 0;
                var cappedByTerminatingEndpoint = 0;

                bool IsUsecThirtyLayerName(string layer)
                {
                    return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
                }

                bool TryGetNearestThirtyAxis(bool wantVertical, Point2d referencePoint, out double axis)
                {
                    axis = 0.0;
                    var found = false;
                    var bestDistance = double.MaxValue;
                    const double axisRangeTol = 4.0;
                    for (var i = 0; i < allRoadSegments.Count; i++)
                    {
                        var seg = allRoadSegments[i];
                        if (!IsUsecThirtyLayerName(seg.Layer))
                        {
                            continue;
                        }

                        var horizontal = IsHorizontalLike(seg.A, seg.B);
                        var vertical = IsVerticalLike(seg.A, seg.B);
                        if (wantVertical && !vertical)
                        {
                            continue;
                        }

                        if (!wantVertical && !horizontal)
                        {
                            continue;
                        }

                        if (wantVertical)
                        {
                            var minY = Math.Min(seg.A.Y, seg.B.Y) - axisRangeTol;
                            var maxY = Math.Max(seg.A.Y, seg.B.Y) + axisRangeTol;
                            if (referencePoint.Y < minY || referencePoint.Y > maxY)
                            {
                                continue;
                            }

                            var d = Math.Abs(referencePoint.X - (0.5 * (seg.A.X + seg.B.X)));
                            if (d >= bestDistance)
                            {
                                continue;
                            }

                            bestDistance = d;
                            axis = 0.5 * (seg.A.X + seg.B.X);
                            found = true;
                            continue;
                        }

                        var minX = Math.Min(seg.A.X, seg.B.X) - axisRangeTol;
                        var maxX = Math.Max(seg.A.X, seg.B.X) + axisRangeTol;
                        if (referencePoint.X < minX || referencePoint.X > maxX)
                        {
                            continue;
                        }

                        var dY = Math.Abs(referencePoint.Y - (0.5 * (seg.A.Y + seg.B.Y)));
                        if (dY >= bestDistance)
                        {
                            continue;
                        }

                        bestDistance = dY;
                        axis = 0.5 * (seg.A.Y + seg.B.Y);
                        found = true;
                    }

                    return found;
                }

                bool HasCompanionAtExpectedOffset(
                    ObjectId sourceId,
                    Point2d sourceA,
                    Point2d sourceB,
                    string companionLayer,
                    double expectedOffsetMeters,
                    double offsetToleranceMeters)
                {
                    var sourceVec = sourceB - sourceA;
                    var sourceLen = sourceVec.Length;
                    if (sourceLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceUnit = sourceVec / sourceLen;
                    var sourceNormal = new Vector2d(-sourceUnit.Y, sourceUnit.X);
                    var sourceMid = Midpoint(sourceA, sourceB);
                    for (var i = 0; i < allRoadSegments.Count; i++)
                    {
                        var seg = allRoadSegments[i];
                        if (seg.Id == sourceId ||
                            !string.Equals(seg.Layer, companionLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var segVec = seg.B - seg.A;
                        var segLen = segVec.Length;
                        if (segLen <= 1e-6)
                        {
                            continue;
                        }

                        var segUnit = segVec / segLen;
                        if (Math.Abs(sourceUnit.DotProduct(segUnit)) < 0.995)
                        {
                            continue;
                        }

                        var segMid = Midpoint(seg.A, seg.B);
                        var lateral = Math.Abs((segMid - sourceMid).DotProduct(sourceNormal));
                        if (Math.Abs(lateral - expectedOffsetMeters) > offsetToleranceMeters)
                        {
                            continue;
                        }

                        var s0 = (seg.A - sourceA).DotProduct(sourceUnit);
                        var s1 = (seg.B - sourceA).DotProduct(sourceUnit);
                        var overlapMin = Math.Max(0.0, Math.Min(s0, s1));
                        var overlapMax = Math.Min(sourceLen, Math.Max(s0, s1));
                        var overlapLen = overlapMax - overlapMin;
                        var minOverlap = Math.Max(20.0, Math.Min(sourceLen, segLen) * 0.30);
                        if (overlapLen < minOverlap)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool IsSectionLineCandidate(ObjectId sourceId, Point2d sourceA, Point2d sourceB, string sourceLayer)
                {
                    if (!IsSectionLineLayer(sourceLayer))
                    {
                        return false;
                    }

                    const double scaledSectionGapMeters = 20.12;
                    const double scaledThirtyToTwentyGapMeters = 10.06;
                    const double offsetToleranceMeters = 2.50;

                    bool hasTwentyCompanion =
                        HasCompanionAtExpectedOffset(sourceId, sourceA, sourceB, LayerUsecTwenty, RoadAllowanceSecWidthMeters, offsetToleranceMeters) ||
                        HasCompanionAtExpectedOffset(sourceId, sourceA, sourceB, LayerUsecTwenty, scaledSectionGapMeters, offsetToleranceMeters);
                    bool hasZeroCompanion =
                        HasCompanionAtExpectedOffset(sourceId, sourceA, sourceB, LayerUsecZero, RoadAllowanceSecWidthMeters, offsetToleranceMeters) ||
                        HasCompanionAtExpectedOffset(sourceId, sourceA, sourceB, LayerUsecZero, scaledSectionGapMeters, offsetToleranceMeters);

                    if (string.Equals(sourceLayer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                    {
                        return hasTwentyCompanion;
                    }

                    if (string.Equals(sourceLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                    {
                        return hasZeroCompanion ||
                               HasCompanionAtExpectedOffset(sourceId, sourceA, sourceB, LayerUsecThirty, CorrectionLinePairGapMeters, offsetToleranceMeters) ||
                               HasCompanionAtExpectedOffset(sourceId, sourceA, sourceB, LayerUsecThirty, scaledThirtyToTwentyGapMeters, offsetToleranceMeters);
                    }

                    return false;
                }

                int SignWithTol(double value, double tol)
                {
                    if (value > tol)
                    {
                        return 1;
                    }

                    if (value < -tol)
                    {
                        return -1;
                    }

                    return 0;
                }

                bool IsSameSideOfRoadAllowance(
                    (ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool MovableSource, bool IsBaseUsecTarget, string Layer) source,
                    Point2d sourcePoint,
                    Point2d sourceOtherPoint,
                    Point2d targetPoint)
                {
                    const double sideTol = 0.25;
                    if (source.Vertical)
                    {
                        if (!TryGetNearestThirtyAxis(wantVertical: false, sourcePoint, out var axisY))
                        {
                            return true;
                        }

                        var sourceSign = SignWithTol(sourcePoint.Y - axisY, sideTol);
                        if (sourceSign == 0)
                        {
                            sourceSign = SignWithTol(sourceOtherPoint.Y - axisY, sideTol);
                        }

                        var targetSign = SignWithTol(targetPoint.Y - axisY, sideTol);
                        if (sourceSign == 0 || targetSign == 0)
                        {
                            return true;
                        }

                        return sourceSign == targetSign;
                    }

                    if (source.Horizontal)
                    {
                        if (!TryGetNearestThirtyAxis(wantVertical: true, sourcePoint, out var axisX))
                        {
                            return true;
                        }

                        var sourceSign = SignWithTol(sourcePoint.X - axisX, sideTol);
                        if (sourceSign == 0)
                        {
                            sourceSign = SignWithTol(sourceOtherPoint.X - axisX, sideTol);
                        }

                        var targetSign = SignWithTol(targetPoint.X - axisX, sideTol);
                        if (sourceSign == 0 || targetSign == 0)
                        {
                            return true;
                        }

                        return sourceSign == targetSign;
                    }

                    return false;
                }

                (bool TouchesSameBand, bool TouchesUsecThirty) GetEndpointTouchState(
                    Point2d endpoint,
                    ObjectId sourceId,
                    string sourceLayer)
                {
                    var touchesSameBand = false;
                    var touchesUsecThirty = false;
                    for (var i = 0; i < allRoadSegments.Count; i++)
                    {
                        var seg = allRoadSegments[i];
                        if (seg.Id == sourceId)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, seg.A, seg.B) <= endpointHitTol)
                        {
                            if (string.Equals(seg.Layer, sourceLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                touchesSameBand = true;
                            }
                            else if (IsUsecThirtyLayerName(seg.Layer))
                            {
                                touchesUsecThirty = true;
                            }
                        }
                    }

                    return (touchesSameBand, touchesUsecThirty);
                }

                void UpdateTrackedSegment(ObjectId id, Point2d a, Point2d b)
                {
                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    for (var i = 0; i < trackedSegments.Count; i++)
                    {
                        if (trackedSegments[i].Id != id)
                        {
                            continue;
                        }

                        trackedSegments[i] = (id, a, b, horizontal, vertical, trackedSegments[i].MovableSource, trackedSegments[i].IsBaseUsecTarget, trackedSegments[i].Layer);
                        break;
                    }

                    for (var i = 0; i < allRoadSegments.Count; i++)
                    {
                        if (allRoadSegments[i].Id != id)
                        {
                            continue;
                        }

                        allRoadSegments[i] = (id, a, b, allRoadSegments[i].Layer);
                        break;
                    }
                }

                bool TryGetTerminatingEndpointCap(
                    (ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool MovableSource, bool IsBaseUsecTarget, string Layer) source,
                    Point2d endpoint,
                    Vector2d outwardUnit,
                    double minAlong,
                    double maxAlong,
                    out Point2d capPoint,
                    out double capAlong)
                {
                    var bestCapPoint = endpoint;
                    var bestCapAlong = double.MaxValue;

                    var sourceVec = source.B - source.A;
                    var sourceLen = sourceVec.Length;
                    if (sourceLen <= 1e-6)
                    {
                        capPoint = endpoint;
                        capAlong = double.MaxValue;
                        return false;
                    }

                    var sourceAxis = sourceVec / sourceLen;
                    const double axisTouchTol = 0.45;
                    const double axisOffTol = 1.50;
                    const double nonCollinearDotMax = 0.98;
                    var found = false;

                    bool IsCapTerminatorLayer(string layer)
                    {
                        return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
                    }

                    bool TryCandidate(Point2d touchingEndpoint, Point2d otherEndpoint)
                    {
                        var touchLateral = DistancePointToInfiniteLine(touchingEndpoint, source.A, source.B);
                        if (touchLateral > axisTouchTol)
                        {
                            return false;
                        }

                        // Only treat it as a terminator when the opposite endpoint is off the source axis.
                        var otherLateral = DistancePointToInfiniteLine(otherEndpoint, source.A, source.B);
                        if (otherLateral <= axisOffTol)
                        {
                            return false;
                        }

                        var along = (touchingEndpoint - endpoint).DotProduct(outwardUnit);
                        if (along <= minAlong || along > maxAlong)
                        {
                            return false;
                        }

                        var projected = endpoint + (outwardUnit * along);
                        if (!IsPointInAnyWindow(projected))
                        {
                            return false;
                        }

                        if (along >= bestCapAlong)
                        {
                            return false;
                        }

                        bestCapAlong = along;
                        bestCapPoint = projected;
                        found = true;
                        return true;
                    }

                    for (var i = 0; i < allRoadSegments.Count; i++)
                    {
                        var seg = allRoadSegments[i];
                        if (seg.Id == source.Id || !IsCapTerminatorLayer(seg.Layer))
                        {
                            continue;
                        }

                        var segVec = seg.B - seg.A;
                        var segLen = segVec.Length;
                        if (segLen <= 1e-6)
                        {
                            continue;
                        }

                        var segAxis = segVec / segLen;
                        if (Math.Abs(sourceAxis.DotProduct(segAxis)) > nonCollinearDotMax)
                        {
                            continue;
                        }

                        TryCandidate(seg.A, seg.B);
                        TryCandidate(seg.B, seg.A);
                    }

                    capPoint = bestCapPoint;
                    capAlong = bestCapAlong;
                    return found;
                }

                for (var si = 0; si < trackedSegments.Count; si++)
                {
                    var source = trackedSegments[si];
                    if (!source.MovableSource)
                    {
                        continue;
                    }

                    if (!source.Horizontal && !source.Vertical)
                    {
                        continue;
                    }

                    if (!IsSectionLineCandidate(source.Id, source.A, source.B, source.Layer))
                    {
                        nonSectionSourceSkipped++;
                        continue;
                    }

                    // Rule #7 (0/20.12 section-line intersection):
                    // only section-line candidates are allowed to close cross intersections.

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        scannedEndpoints++;
                        var endpoint = endpointIndex == 0 ? source.A : source.B;
                        var other = endpointIndex == 0 ? source.B : source.A;

                        if (IsPointOnAnyWindowBoundary(endpoint, outerBoundaryTol))
                        {
                            skippedOnOuterBoundary++;
                            continue;
                        }

                        var touchState = GetEndpointTouchState(endpoint, source.Id, source.Layer);
                        if (touchState.TouchesSameBand)
                        {
                            alreadyConnected++;
                            continue;
                        }
                        if (!touchState.TouchesUsecThirty)
                        {
                            // Allow dangling 0/20 endpoints to connect even without explicit 30.18 touch.
                            // This keeps partial township builds consistent with full-township intersections.
                            noThirtyContext++;
                        }

                        var outward = endpoint - other;
                        var outwardLen = outward.Length;
                        if (outwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var outwardUnit = outward / outwardLen;
                        var found = false;
                        var bestScore = double.MaxValue;
                        var bestTarget = endpoint;

                        bool TrySelectBestTarget(
                            bool enforceSideRule,
                            bool allowBaseUsecTarget,
                            double targetTol,
                            bool allowApparent,
                            bool allowCrossLayer,
                            out int rejectedBySideThisPass)
                        {
                            rejectedBySideThisPass = 0;
                            var localFound = false;
                            var localBestScore = double.MaxValue;
                            var localBestTarget = endpoint;
                            for (var ti = 0; ti < trackedSegments.Count; ti++)
                            {
                                var target = trackedSegments[ti];
                                if (target.Id == source.Id)
                                {
                                    continue;
                                }

                                if (!allowBaseUsecTarget && target.IsBaseUsecTarget)
                                {
                                    continue;
                                }

                                var orthogonal =
                                    (source.Horizontal && target.Vertical) ||
                                    (source.Vertical && target.Horizontal);
                                if (!orthogonal)
                                {
                                    continue;
                                }

                                if (!IsSectionLineCandidate(target.Id, target.A, target.B, target.Layer))
                                {
                                    nonSectionTargetRejected++;
                                    continue;
                                }

                                if (!TryIntersectInfiniteLines(source.A, source.B, target.A, target.B, out var intersection))
                                {
                                    continue;
                                }

                                if (!IsPointInAnyWindow(intersection))
                                {
                                    continue;
                                }

                                if (enforceSideRule &&
                                    !IsSameSideOfRoadAllowance(source, endpoint, other, intersection))
                                {
                                    rejectedBySideThisPass++;
                                    continue;
                                }

                                var along = (intersection - endpoint).DotProduct(outwardUnit);
                                if (along <= minExtend || along > maxExtend)
                                {
                                    continue;
                                }

                                var sameLayer = string.Equals(source.Layer, target.Layer, StringComparison.OrdinalIgnoreCase);
                                if (!sameLayer)
                                {
                                    var sourceIsZeroTwenty = IsUsecZeroOrTwentyLayer(source.Layer);
                                    var targetIsZeroTwenty = IsUsecZeroOrTwentyLayer(target.Layer);
                                    if (!allowCrossLayer || !sourceIsZeroTwenty || !targetIsZeroTwenty)
                                    {
                                        continue;
                                    }
                                }

                                var targetDistance = DistancePointToSegment(intersection, target.A, target.B);
                                var endpointGap = Math.Min(
                                    intersection.GetDistanceTo(target.A),
                                    intersection.GetDistanceTo(target.B));
                                var isApparent =
                                    targetDistance > targetTol &&
                                    allowApparent &&
                                    endpointGap <= apparentEndpointGapTol;

                                if (targetDistance > targetTol && !isApparent)
                                {
                                    continue;
                                }

                                var score = along + (4.0 * targetDistance);
                                if (!sameLayer)
                                {
                                    // Prefer same-band continuity first; only use 0<->20 when no same-band
                                    // target satisfies the endpoint constraints.
                                    score += 12.0;
                                }
                                if (isApparent)
                                {
                                    // Prefer true on-segment targets, but allow short open-T/perpendicular
                                    // pairs to resolve by apparent intersection when both are 0/20.
                                    score += (8.0 * endpointGap) + 20.0;
                                }

                                if (score >= localBestScore)
                                {
                                    continue;
                                }

                                localFound = true;
                                localBestScore = score;
                                localBestTarget = intersection;
                            }

                            if (localFound)
                            {
                                found = true;
                                bestScore = localBestScore;
                                bestTarget = localBestTarget;
                            }

                            return localFound;
                        }

                        if (!TrySelectBestTarget(
                                enforceSideRule: true,
                                allowBaseUsecTarget: false,
                                targetTol: targetOnSegmentTol,
                                allowApparent: false,
                                allowCrossLayer: false,
                                out var rejectedBySide))
                        {
                            sideRejected += rejectedBySide;
                            // Fallback: if side-of-road inference is ambiguous for this endpoint,
                            // still connect to the nearest valid orthogonal 0/20 target.
                            if (TrySelectBestTarget(
                                enforceSideRule: false,
                                allowBaseUsecTarget: false,
                                targetTol: targetOnSegmentTol,
                                allowApparent: false,
                                allowCrossLayer: false,
                                out _))
                            {
                                sideRuleFallbackUsed++;
                            }
                            else if (TrySelectBestTarget(
                                enforceSideRule: false,
                                allowBaseUsecTarget: false,
                                targetTol: targetOnSegmentTolRelaxed,
                                allowApparent: true,
                                allowCrossLayer: false,
                                out _))
                            {
                                sideRuleFallbackUsed++;
                                apparentFallbackUsed++;
                            }
                            else if (TrySelectBestTarget(
                                enforceSideRule: false,
                                allowBaseUsecTarget: true,
                                targetTol: targetOnSegmentTolRelaxed,
                                allowApparent: false,
                                allowCrossLayer: false,
                                out _))
                            {
                                sideRuleFallbackUsed++;
                                baseTargetFallbackUsed++;
                            }
                            else if (TrySelectBestTarget(
                                enforceSideRule: false,
                                allowBaseUsecTarget: false,
                                targetTol: targetOnSegmentTol,
                                allowApparent: false,
                                allowCrossLayer: true,
                                out _))
                            {
                                crossLayerFallbackUsed++;
                            }
                            else if (TrySelectBestTarget(
                                enforceSideRule: false,
                                allowBaseUsecTarget: false,
                                targetTol: targetOnSegmentTolRelaxed,
                                allowApparent: true,
                                allowCrossLayer: true,
                                out _))
                            {
                                crossLayerFallbackUsed++;
                                apparentFallbackUsed++;
                            }
                        }
                        else
                        {
                            sideRejected += rejectedBySide;
                        }

                        if (!found)
                        {
                            noTarget++;
                            continue;
                        }

                        var bestAlong = (bestTarget - endpoint).DotProduct(outwardUnit);
                        if (bestAlong > minExtend &&
                            TryGetTerminatingEndpointCap(
                                source,
                                endpoint,
                                outwardUnit,
                                minExtend,
                                bestAlong,
                                out var cappedTarget,
                                out var cappedAlong) &&
                            cappedAlong < (bestAlong - 1e-6))
                        {
                            bestTarget = cappedTarget;
                            bestScore = Math.Min(bestScore, cappedAlong);
                            cappedByTerminatingEndpoint++;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var curA, out var curB))
                        {
                            continue;
                        }

                        var d0 = curA.GetDistanceTo(endpoint);
                        var d1 = curB.GetDistanceTo(endpoint);
                        if (d0 > endpointSnapTol && d1 > endpointSnapTol)
                        {
                            continue;
                        }

                        var moveStart = d0 <= d1;
                        var sourceEndpoint = moveStart ? curA : curB;
                        var moveDistance = sourceEndpoint.GetDistanceTo(bestTarget);
                        if (moveDistance <= endpointMoveTol || moveDistance > maxExtend)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, moveStart, bestTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var newA, out var newB))
                        {
                            continue;
                        }

                        moved++;
                        UpdateTrackedSegment(source.Id, newA, newB);
                        source = trackedSegments[si];
                    }
                }

                tr.Commit();
                if (moved > 0 || noTarget > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: 0/20 dangling endpoint connect scanned={scannedEndpoints}, outerSkipped={skippedOnOuterBoundary}, alreadyConnected={alreadyConnected}, no3018Context={noThirtyContext}, sideRejected={sideRejected}, sideFallbackUsed={sideRuleFallbackUsed}, crossLayerFallbackUsed={crossLayerFallbackUsed}, apparentFallbackUsed={apparentFallbackUsed}, baseFallbackUsed={baseTargetFallbackUsed}, sectionSourceSkipped={nonSectionSourceSkipped}, nonSectionTargetRejected={nonSectionTargetRejected}, capLimited={cappedByTerminatingEndpoint}, moved={moved}, noTarget={noTarget}.");
                }
            }
        }

        private static void CleanupOverlappingZeroTwentySectionLines(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool IsZeroTwentyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, Point2d Mid, double Len, bool Horizontal, bool Vertical)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsZeroTwentyLayer(layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 2.0 || len > 2500.0)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    candidates.Add((id, layer, a, b, Midpoint(a, b), len, horizontal, vertical));
                }

                if (candidates.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTol = 0.75;
                const double midpointTol = 0.80;
                const double lengthTol = 0.80;
                const double containTol = 0.60;

                bool IsSegmentContained(Point2d innerA, Point2d innerB, Point2d outerA, Point2d outerB)
                {
                    return DistancePointToSegment(innerA, outerA, outerB) <= containTol &&
                           DistancePointToSegment(innerB, outerA, outerB) <= containTol;
                }

                var toErase = new HashSet<ObjectId>();
                for (var i = 0; i < candidates.Count; i++)
                {
                    var a = candidates[i];
                    if (toErase.Contains(a.Id))
                    {
                        continue;
                    }

                    for (var j = i + 1; j < candidates.Count; j++)
                    {
                        var b = candidates[j];
                        if (toErase.Contains(b.Id))
                        {
                            continue;
                        }

                        if (!string.Equals(a.Layer, b.Layer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var sameAxis = (a.Horizontal && b.Horizontal) || (a.Vertical && b.Vertical);
                        if (!sameAxis)
                        {
                            continue;
                        }

                        var nearDuplicate = AreSegmentEndpointsNear(a.A, a.B, b.A, b.B, endpointTol);
                        var collinearOverlap = AreSegmentsDuplicateOrCollinearOverlap(a.A, a.B, b.A, b.B);
                        if (!nearDuplicate && !collinearOverlap)
                        {
                            continue;
                        }

                        var aContainsB = IsSegmentContained(b.A, b.B, a.A, a.B);
                        var bContainsA = IsSegmentContained(a.A, a.B, b.A, b.B);
                        var similarShape =
                            Math.Abs(a.Len - b.Len) <= lengthTol &&
                            a.Mid.GetDistanceTo(b.Mid) <= midpointTol;
                        if (!nearDuplicate && !aContainsB && !bContainsA && !similarShape)
                        {
                            continue;
                        }

                        ObjectId eraseId;
                        if (aContainsB && !bContainsA)
                        {
                            eraseId = b.Id;
                        }
                        else if (bContainsA && !aContainsB)
                        {
                            eraseId = a.Id;
                        }
                        else if (Math.Abs(a.Len - b.Len) > lengthTol)
                        {
                            eraseId = a.Len < b.Len ? a.Id : b.Id;
                        }
                        else
                        {
                            eraseId = a.Id.Handle.Value > b.Id.Handle.Value ? a.Id : b.Id;
                        }

                        toErase.Add(eraseId);
                    }
                }

                var erased = 0;
                foreach (var id in toErase)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Erase();
                    erased++;
                }

                tr.Commit();
                if (erased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {erased} overlapping 0/20 section-line segment(s) after cross-connect.");
                }
            }
        }

        private static void EnforceSectionLineNoCrossingRules(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable is Line ln)
                {
                    var old = moveStart
                        ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                        : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        ln.StartPoint = new Point3d(target.X, target.Y, ln.StartPoint.Z);
                    }
                    else
                    {
                        ln.EndPoint = new Point3d(target.X, target.Y, ln.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices >= 2)
                {
                    var index = moveStart ? 0 : pl.NumberOfVertices - 1;
                    var old = pl.GetPoint2dAt(index);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(index, target);
                    return true;
                }

                return false;
            }

            bool IsSectionTypeLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
            }

            bool IsZeroTwentyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
            }

            bool IsThirtyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
            }

            bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection, out double tA, out double tB)
            {
                intersection = default;
                tA = 0.0;
                tB = 0.0;
                var da = a1 - a0;
                var db = b1 - b0;
                var denom = Cross2d(da, db);
                if (Math.Abs(denom) <= 1e-9)
                {
                    return false;
                }

                var diff = b0 - a0;
                tA = Cross2d(diff, db) / denom;
                tB = Cross2d(diff, da) / denom;
                if (tA < 0.0 || tA > 1.0 || tB < 0.0 || tB > 1.0)
                {
                    return false;
                }

                intersection = a0 + (da * tA);
                return true;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Alive)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsSectionTypeLayer(layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    segments.Add((id, layer, a, b, true));
                }

                if (segments.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                const double paramTol = 0.02;
                const double endpointMoveTol = 0.05;
                const double maxMove = 120.0;
                const double blindTouchTol = 0.80;
                const double endpointSnapTol = 0.65;
                const double endpointOffAxisTol = 0.80;
                var adjustedZeroTwenty = 0;
                var adjustedThirty = 0;
                var adjustedByCrossing = 0;
                var adjustedByTerminator = 0;

                int ZeroTwentyTargetPriority(string layer)
                {
                    if (IsZeroTwentyLayer(layer))
                    {
                        return 0;
                    }

                    if (string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        return 1;
                    }

                    if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        return 2;
                    }

                    return 3;
                }

                bool IsBlindIndex(int index)
                {
                    var source = segments[index];
                    if (!source.Alive)
                    {
                        return false;
                    }

                    bool endpointATouches = false;
                    bool endpointBTouches = false;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        if (i == index || !segments[i].Alive)
                        {
                            continue;
                        }

                        var other = segments[i];
                        if (DistancePointToSegment(source.A, other.A, other.B) <= blindTouchTol)
                        {
                            endpointATouches = true;
                        }

                        if (DistancePointToSegment(source.B, other.A, other.B) <= blindTouchTol)
                        {
                            endpointBTouches = true;
                        }

                        if (endpointATouches && endpointBTouches)
                        {
                            return false;
                        }
                    }

                    return !(endpointATouches && endpointBTouches);
                }

                for (var iteration = 0; iteration < 3; iteration++)
                {
                    var movedAny = false;
                    for (var si = 0; si < segments.Count; si++)
                    {
                        var source = segments[si];
                        if (!source.Alive)
                        {
                            continue;
                        }

                        var sourceIsZeroTwenty = IsZeroTwentyLayer(source.Layer);
                        var sourceIsThirty = IsThirtyLayer(source.Layer);
                        if (!sourceIsZeroTwenty && !sourceIsThirty)
                        {
                            continue;
                        }

                        var bestFound = false;
                        var bestMoveDistance = double.MaxValue;
                        var bestMoveStart = false;
                        var bestTargetPoint = default(Point2d);
                        var bestTargetPriority = int.MaxValue;
                        var bestTargetHandle = long.MaxValue;
                        var bestByCrossing = false;
                        var bestByTerminator = false;

                        for (var ti = 0; ti < segments.Count; ti++)
                        {
                            if (ti == si || !segments[ti].Alive)
                            {
                                continue;
                            }

                            var target = segments[ti];
                            if (!IsSectionTypeLayer(target.Layer))
                            {
                                continue;
                            }

                            if (sourceIsThirty)
                            {
                                if (string.Equals(target.Layer, source.Layer, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                // Rule: 30.18 can only cross blind/LSD. LSD is excluded from section-type layers.
                                if (IsBlindIndex(ti))
                                {
                                    continue;
                                }
                            }
                            else if (sourceIsZeroTwenty)
                            {
                                // Rule: 0/20.12 cannot cross through any other section-type line.
                                if (IsThirtyLayer(target.Layer))
                                {
                                    // 0/20.12 must never terminate on 30.18.
                                    continue;
                                }
                            }

                            if (!TryIntersectSegments(source.A, source.B, target.A, target.B, out var p, out var tS, out var tT))
                            {
                                continue;
                            }

                            var sourceInterior = tS > paramTol && tS < (1.0 - paramTol);
                            var targetInterior = tT > paramTol && tT < (1.0 - paramTol);
                            var targetEndpointTouch = tT <= paramTol || tT >= (1.0 - paramTol);
                            // Rule #7: 0/20 section lines form seamless crosses. Do not trim interior
                            // 0/20 intersections; only trim when another line endpoint terminates on source.
                            var enforceByCrossing = sourceIsThirty && sourceInterior && targetInterior;
                            var enforceByTerminator = false;
                            var targetIsTerminatorForSource = sourceIsZeroTwenty
                                ? string.Equals(target.Layer, source.Layer, StringComparison.OrdinalIgnoreCase)
                                : (IsSectionTypeLayer(target.Layer) && !IsThirtyLayer(target.Layer));
                            if (!enforceByCrossing && sourceInterior && targetEndpointTouch && targetIsTerminatorForSource)
                            {
                                var dToA = p.GetDistanceTo(target.A);
                                var dToB = p.GetDistanceTo(target.B);
                                var targetEndpointGap = Math.Min(dToA, dToB);
                                if (targetEndpointGap <= endpointSnapTol)
                                {
                                    var touchesA = dToA <= dToB;
                                    var oppositeEndpoint = touchesA ? target.B : target.A;
                                    var oppositeOffset = DistancePointToInfiniteLine(oppositeEndpoint, source.A, source.B);
                                    // Rule: do not let 0/20 or 30 pass through where another section line terminates on it.
                                    // Keep collinear same-axis continuation out of this trim.
                                    enforceByTerminator = oppositeOffset > endpointOffAxisTol;
                                }
                            }

                            if (!enforceByCrossing && !enforceByTerminator)
                            {
                                continue;
                            }

                            var dA = source.A.GetDistanceTo(p);
                            var dB = source.B.GetDistanceTo(p);
                            var moveStart = dA <= dB;
                            var sourceEndpoint = moveStart ? source.A : source.B;
                            var moveDistance = sourceEndpoint.GetDistanceTo(p);
                            if (moveDistance <= endpointMoveTol || moveDistance > maxMove)
                            {
                                continue;
                            }

                            var candidatePriority = sourceIsZeroTwenty
                                ? ZeroTwentyTargetPriority(target.Layer)
                                : 0;
                            var candidateHandle = target.Id.Handle.Value;
                            var betterCandidate =
                                !bestFound ||
                                candidatePriority < bestTargetPriority ||
                                (candidatePriority == bestTargetPriority &&
                                 moveDistance < (bestMoveDistance - 1e-6)) ||
                                (candidatePriority == bestTargetPriority &&
                                 Math.Abs(moveDistance - bestMoveDistance) <= 1e-6 &&
                                 candidateHandle < bestTargetHandle);
                            if (!betterCandidate)
                            {
                                continue;
                            }

                            bestFound = true;
                            bestMoveDistance = moveDistance;
                            bestMoveStart = moveStart;
                            bestTargetPoint = p;
                            bestTargetPriority = candidatePriority;
                            bestTargetHandle = candidateHandle;
                            bestByCrossing = enforceByCrossing;
                            bestByTerminator = enforceByTerminator;
                        }

                        if (!bestFound)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, bestMoveStart, bestTargetPoint, endpointMoveTol))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var newA, out var newB))
                        {
                            continue;
                        }

                        segments[si] = (source.Id, source.Layer, newA, newB, true);
                        movedAny = true;
                        if (bestByCrossing)
                        {
                            adjustedByCrossing++;
                        }
                        else if (bestByTerminator)
                        {
                            adjustedByTerminator++;
                        }
                        if (sourceIsZeroTwenty)
                        {
                            adjustedZeroTwenty++;
                        }
                        else
                        {
                            adjustedThirty++;
                        }
                    }

                    if (!movedAny)
                    {
                        break;
                    }
                }

                tr.Commit();
                if (adjustedZeroTwenty > 0 || adjustedThirty > 0)
                {
                    logger?.WriteLine($"Cleanup: no-crossing enforcement adjusted 0/20={adjustedZeroTwenty}, 30={adjustedThirty} section-line endpoint(s) [cross={adjustedByCrossing}, terminator={adjustedByTerminator}].");
                }
            }
        }

        private static void TrimZeroTwentyPassThroughExtensions(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable is Line ln)
                {
                    var old = moveStart
                        ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                        : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        ln.StartPoint = new Point3d(target.X, target.Y, ln.StartPoint.Z);
                    }
                    else
                    {
                        ln.EndPoint = new Point3d(target.X, target.Y, ln.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices >= 2)
                {
                    var index = moveStart ? 0 : pl.NumberOfVertices - 1;
                    var old = pl.GetPoint2dAt(index);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(index, target);
                    return true;
                }

                return false;
            }

            bool IsZeroTwentyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
            }

            bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection, out double tA, out double tB)
            {
                intersection = default;
                tA = 0.0;
                tB = 0.0;
                var da = a1 - a0;
                var db = b1 - b0;
                var denom = Cross2d(da, db);
                if (Math.Abs(denom) <= 1e-9)
                {
                    return false;
                }

                var diff = b0 - a0;
                tA = Cross2d(diff, db) / denom;
                tB = Cross2d(diff, da) / denom;
                if (tA < 0.0 || tA > 1.0 || tB < 0.0 || tB > 1.0)
                {
                    return false;
                }

                intersection = a0 + (da * tA);
                return true;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Alive)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsZeroTwentyLayer(layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    segments.Add((id, layer, a, b, true));
                }

                if (segments.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double maxOverhangTrim = 80.0;
                const double endpointTouchTol = 1.50;
                const double outerBoundaryTol = 0.40;
                const double sourceInteriorTol = 1e-4;
                var adjusted = 0;
                var scannedDanglingEndpoints = 0;
                var candidateHits = 0;

                bool EndpointTouchesAnySameBandEndpoint(Point2d endpoint, ObjectId sourceId)
                {
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var other = segments[i];
                        if (!other.Alive || other.Id == sourceId)
                        {
                            continue;
                        }

                        if (endpoint.GetDistanceTo(other.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(other.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                for (var iteration = 0; iteration < 4; iteration++)
                {
                    var movedAny = false;
                    for (var si = 0; si < segments.Count; si++)
                    {
                        var source = segments[si];
                        if (!source.Alive)
                        {
                            continue;
                        }

                        var bestFound = false;
                        var bestMoveDistance = double.MaxValue;
                        var bestMoveStart = false;
                        var bestTargetPoint = default(Point2d);
                        var bestTargetHandle = long.MaxValue;
                        var endpointAConnected = EndpointTouchesAnySameBandEndpoint(source.A, source.Id);
                        var endpointBConnected = EndpointTouchesAnySameBandEndpoint(source.B, source.Id);
                        var endpointAOnBoundary = IsPointOnAnyWindowBoundary(source.A, outerBoundaryTol);
                        var endpointBOnBoundary = IsPointOnAnyWindowBoundary(source.B, outerBoundaryTol);
                        var canTrimStart = !endpointAOnBoundary && !endpointAConnected && (endpointBConnected || endpointBOnBoundary);
                        var canTrimEnd = !endpointBOnBoundary && !endpointBConnected && (endpointAConnected || endpointAOnBoundary);
                        if (canTrimStart)
                        {
                            scannedDanglingEndpoints++;
                        }

                        if (canTrimEnd)
                        {
                            scannedDanglingEndpoints++;
                        }

                        for (var ti = 0; ti < segments.Count; ti++)
                        {
                            if (ti == si || !segments[ti].Alive)
                            {
                                continue;
                            }

                            var target = segments[ti];
                            var hasCandidate = false;
                            var p = default(Point2d);
                            if (TryIntersectSegments(source.A, source.B, target.A, target.B, out var intersection, out var tS, out _))
                            {
                                var sourceInterior = tS > sourceInteriorTol && tS < (1.0 - sourceInteriorTol);
                                if (sourceInterior)
                                {
                                    p = intersection;
                                    hasCandidate = true;
                                }
                            }

                            if (!hasCandidate)
                            {
                                Point2d bestEndpointOnSource = default;
                                var bestEndpointScore = double.MaxValue;

                                void ConsiderEndpointCandidate(Point2d candidate)
                                {
                                    if (DistancePointToSegment(candidate, source.A, source.B) > endpointTouchTol)
                                    {
                                        return;
                                    }

                                    var dToA = source.A.GetDistanceTo(candidate);
                                    var dToB = source.B.GetDistanceTo(candidate);
                                    // Candidate must be on source span interior (not already at source endpoint).
                                    if (dToA <= endpointMoveTol || dToB <= endpointMoveTol)
                                    {
                                        return;
                                    }

                                    var score = Math.Min(dToA, dToB);
                                    if (score >= bestEndpointScore)
                                    {
                                        return;
                                    }

                                    bestEndpointOnSource = candidate;
                                    bestEndpointScore = score;
                                    hasCandidate = true;
                                }

                                ConsiderEndpointCandidate(target.A);
                                ConsiderEndpointCandidate(target.B);
                                if (hasCandidate)
                                {
                                    p = bestEndpointOnSource;
                                }
                            }

                            if (!hasCandidate)
                            {
                                continue;
                            }

                            var dA = source.A.GetDistanceTo(p);
                            var dB = source.B.GetDistanceTo(p);
                            var moveStart = dA <= dB;
                            if ((moveStart && !canTrimStart) || (!moveStart && !canTrimEnd))
                            {
                                continue;
                            }

                            var moveDistance = moveStart ? dA : dB;
                            if (moveDistance <= endpointMoveTol || moveDistance > maxOverhangTrim)
                            {
                                continue;
                            }
                            candidateHits++;

                            var candidateHandle = target.Id.Handle.Value;
                            var betterCandidate =
                                !bestFound ||
                                moveDistance < (bestMoveDistance - 1e-6) ||
                                (Math.Abs(moveDistance - bestMoveDistance) <= 1e-6 &&
                                 candidateHandle < bestTargetHandle);
                            if (!betterCandidate)
                            {
                                continue;
                            }

                            bestFound = true;
                            bestMoveDistance = moveDistance;
                            bestMoveStart = moveStart;
                            bestTargetPoint = p;
                            bestTargetHandle = candidateHandle;
                        }

                        if (!bestFound)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, bestMoveStart, bestTargetPoint, endpointMoveTol))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var newA, out var newB))
                        {
                            continue;
                        }

                        segments[si] = (source.Id, source.Layer, newA, newB, true);
                        movedAny = true;
                        adjusted++;
                    }

                    if (!movedAny)
                    {
                        break;
                    }
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: final 0/20 overshoot pass scannedDangling={scannedDanglingEndpoints}, candidateHits={candidateHits}, trimmed={adjusted}.");
            }
        }

        private static void ResolveZeroTwentyOverlapByEndpointIntersection(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool IsPointOnAnyWindowBoundary(Point2d p, double tol)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    var withinX = p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol);
                    var withinY = p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol);
                    if (!withinX || !withinY)
                    {
                        continue;
                    }

                    var onLeft = Math.Abs(p.X - w.MinPoint.X) <= tol;
                    var onRight = Math.Abs(p.X - w.MaxPoint.X) <= tol;
                    var onBottom = Math.Abs(p.Y - w.MinPoint.Y) <= tol;
                    var onTop = Math.Abs(p.Y - w.MaxPoint.Y) <= tol;
                    if (onLeft || onRight || onBottom || onTop)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable is Line ln)
                {
                    var old = moveStart
                        ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                        : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        ln.StartPoint = new Point3d(target.X, target.Y, ln.StartPoint.Z);
                    }
                    else
                    {
                        ln.EndPoint = new Point3d(target.X, target.Y, ln.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices >= 2)
                {
                    var index = moveStart ? 0 : pl.NumberOfVertices - 1;
                    var old = pl.GetPoint2dAt(index);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(index, target);
                    return true;
                }

                return false;
            }

            bool IsZeroTwentyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
            }

            bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection, out double tA, out double tB)
            {
                intersection = default;
                tA = 0.0;
                tB = 0.0;
                var da = a1 - a0;
                var db = b1 - b0;
                var denom = Cross2d(da, db);
                if (Math.Abs(denom) <= 1e-9)
                {
                    return false;
                }

                var diff = b0 - a0;
                tA = Cross2d(diff, db) / denom;
                tB = Cross2d(diff, da) / denom;
                if (tA < 0.0 || tA > 1.0 || tB < 0.0 || tB > 1.0)
                {
                    return false;
                }

                intersection = a0 + (da * tA);
                return true;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Alive)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsZeroTwentyLayer(layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    segments.Add((id, layer, a, b, true));
                }

                if (segments.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double minOverlapMeters = 1.0;
                const double maxOverlapMeters = 40.0;
                const double outerBoundaryTol = 0.40;
                var adjustedPairs = 0;
                var adjustedEndpoints = 0;

                for (var iteration = 0; iteration < 4; iteration++)
                {
                    var movedAny = false;
                    for (var si = 0; si < segments.Count; si++)
                    {
                        var source = segments[si];
                        if (!source.Alive)
                        {
                            continue;
                        }

                        for (var ti = si + 1; ti < segments.Count; ti++)
                        {
                            var target = segments[ti];
                            if (!target.Alive)
                            {
                                continue;
                            }

                            if (!TryIntersectSegments(source.A, source.B, target.A, target.B, out var p, out _, out _))
                            {
                                continue;
                            }

                            var sourceStartDist = source.A.GetDistanceTo(p);
                            var sourceEndDist = source.B.GetDistanceTo(p);
                            var targetStartDist = target.A.GetDistanceTo(p);
                            var targetEndDist = target.B.GetDistanceTo(p);

                            var moveSourceStart = sourceStartDist <= sourceEndDist;
                            var moveTargetStart = targetStartDist <= targetEndDist;
                            var sourceOverlap = moveSourceStart ? sourceStartDist : sourceEndDist;
                            var targetOverlap = moveTargetStart ? targetStartDist : targetEndDist;
                            if (sourceOverlap < minOverlapMeters || sourceOverlap > maxOverlapMeters ||
                                targetOverlap < minOverlapMeters || targetOverlap > maxOverlapMeters)
                            {
                                continue;
                            }

                            var sourceEndpoint = moveSourceStart ? source.A : source.B;
                            var targetEndpoint = moveTargetStart ? target.A : target.B;
                            if (IsPointOnAnyWindowBoundary(sourceEndpoint, outerBoundaryTol) ||
                                IsPointOnAnyWindowBoundary(targetEndpoint, outerBoundaryTol))
                            {
                                continue;
                            }

                            if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity sourceWritable) || sourceWritable.IsErased)
                            {
                                continue;
                            }

                            if (!(tr.GetObject(target.Id, OpenMode.ForWrite, false) is Entity targetWritable) || targetWritable.IsErased)
                            {
                                continue;
                            }

                            var sourceMoved = TryMoveEndpoint(sourceWritable, moveSourceStart, p, endpointMoveTol);
                            var targetMoved = TryMoveEndpoint(targetWritable, moveTargetStart, p, endpointMoveTol);
                            if (!sourceMoved && !targetMoved)
                            {
                                continue;
                            }

                            if (sourceMoved && TryReadOpenSegment(sourceWritable, out var newSourceA, out var newSourceB))
                            {
                                segments[si] = (source.Id, source.Layer, newSourceA, newSourceB, true);
                                adjustedEndpoints++;
                            }

                            if (targetMoved && TryReadOpenSegment(targetWritable, out var newTargetA, out var newTargetB))
                            {
                                segments[ti] = (target.Id, target.Layer, newTargetA, newTargetB, true);
                                adjustedEndpoints++;
                            }

                            adjustedPairs++;
                            movedAny = true;
                        }
                    }

                    if (!movedAny)
                    {
                        break;
                    }
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: final 0/20 overlap snap adjustedPairs={adjustedPairs}, adjustedEndpoints={adjustedEndpoints}, overlapRange=[1,40]m.");
            }
        }

        private static bool TryIntersectInfiniteLineWithSegment(Point2d linePoint, Vector2d lineDir, Point2d segA, Point2d segB, out double tOnLine)
        {
            tOnLine = 0.0;
            var segDir = segB - segA;
            var denom = Cross2d(lineDir, segDir);
            if (Math.Abs(denom) <= 1e-9)
            {
                return false;
            }

            var diff = segA - linePoint;
            var t = Cross2d(diff, segDir) / denom;
            var u = Cross2d(diff, lineDir) / denom;
            if (u < -1e-6 || u > 1.0 + 1e-6)
            {
                return false;
            }

            tOnLine = t;
            return true;
        }

        private static double Cross2d(Vector2d a, Vector2d b)
        {
            return (a.X * b.Y) - (a.Y * b.X);
        }

        private static bool IsAdjustableLsdLineSegment(Point2d a, Point2d b)
        {
            return a.GetDistanceTo(b) >= MinAdjustableLsdLineLengthMeters;
        }

        private static void GetCanonicalSegmentEndpoints(Point2d a, Point2d b, out Point2d first, out Point2d second)
        {
            first = a;
            second = b;
            if (second.X < first.X || (Math.Abs(second.X - first.X) <= 1e-9 && second.Y < first.Y))
            {
                var tmp = first;
                first = second;
                second = tmp;
            }
        }

        private static bool AreSegmentEndpointsNear(Point2d a0, Point2d a1, Point2d b0, Point2d b1, double endpointTol)
        {
            GetCanonicalSegmentEndpoints(a0, a1, out var aFirst, out var aSecond);
            GetCanonicalSegmentEndpoints(b0, b1, out var bFirst, out var bSecond);
            return aFirst.GetDistanceTo(bFirst) <= endpointTol &&
                   aSecond.GetDistanceTo(bSecond) <= endpointTol;
        }

        private static bool AreSegmentsDuplicateOrCollinearOverlap(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
        {
            const double endpointTol = 0.12;
            const double angleTol = 0.003;
            const double distanceTol = 0.25;
            const double overlapTol = 0.20;

            bool Near(Point2d p, Point2d q) => p.GetDistanceTo(q) <= endpointTol;
            if ((Near(a0, b0) && Near(a1, b1)) || (Near(a0, b1) && Near(a1, b0)))
            {
                return true;
            }

            var ua = a1 - a0;
            var ub = b1 - b0;
            var la = ua.Length;
            var lb = ub.Length;
            if (la <= 1e-6 || lb <= 1e-6)
            {
                return false;
            }

            var cross = Math.Abs((ua.X * ub.Y) - (ua.Y * ub.X)) / (la * lb);
            if (cross > angleTol)
            {
                return false;
            }

            if (DistancePointToInfiniteLine(b0, a0, a1) > distanceTol ||
                DistancePointToInfiniteLine(b1, a0, a1) > distanceTol)
            {
                return false;
            }

            var axis = ua / la;
            var aMin = 0.0;
            var aMax = la;
            var tB0 = (b0 - a0).DotProduct(axis);
            var tB1 = (b1 - a0).DotProduct(axis);
            var bMin = Math.Min(tB0, tB1);
            var bMax = Math.Max(tB0, tB1);
            var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            return overlap > overlapTol;
        }

        private static double DistancePointToInfiniteLine(Point2d p, Point2d a, Point2d b)
        {
            var ab = b - a;
            var len = ab.Length;
            if (len <= 1e-9)
            {
                return p.GetDistanceTo(a);
            }

            return Math.Abs((ab.X * (p.Y - a.Y)) - (ab.Y * (p.X - a.X))) / len;
        }

        private static bool TryClipSegmentToWindow(Point2d start, Point2d end, Extents3d? window, out Point2d clippedStart, out Point2d clippedEnd)
        {
            clippedStart = start;
            clippedEnd = end;
            if (!window.HasValue)
            {
                return start.GetDistanceTo(end) > 1e-6;
            }

            var w = window.Value;
            var xmin = w.MinPoint.X;
            var xmax = w.MaxPoint.X;
            var ymin = w.MinPoint.Y;
            var ymax = w.MaxPoint.Y;

            double x0 = start.X;
            double y0 = start.Y;
            double x1 = end.X;
            double y1 = end.Y;
            double dx = x1 - x0;
            double dy = y1 - y0;
            double t0 = 0.0;
            double t1 = 1.0;

            bool Clip(double p, double q)
            {
                if (Math.Abs(p) <= 1e-12)
                {
                    return q >= 0.0;
                }

                var r = q / p;
                if (p < 0.0)
                {
                    if (r > t1) return false;
                    if (r > t0) t0 = r;
                }
                else
                {
                    if (r < t0) return false;
                    if (r < t1) t1 = r;
                }

                return true;
            }

            if (!Clip(-dx, x0 - xmin) ||
                !Clip(dx, xmax - x0) ||
                !Clip(-dy, y0 - ymin) ||
                !Clip(dy, ymax - y0) ||
                t1 < t0)
            {
                return false;
            }

            clippedStart = new Point2d(x0 + t0 * dx, y0 + t0 * dy);
            clippedEnd = new Point2d(x0 + t1 * dx, y0 + t1 * dy);
            return clippedStart.GetDistanceTo(clippedEnd) > 1e-6;
        }

        private static void EnsureDefpointsLayer(Database database, Transaction transaction)
        {
            var lt = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (lt.Has("DEFPOINTS"))
            {
                return;
            }

            lt.UpgradeOpen();
            var rec = new LayerTableRecord
            {
                Name = "DEFPOINTS",
                IsPlottable = false
            };
            lt.Add(rec);
            transaction.AddNewlyCreatedDBObject(rec, true);
        }

        private static string BuildTownshipKey(SectionKey key)
        {
            return $"{key.Zone}|{NormalizeNumberToken(key.Meridian)}|{NormalizeNumberToken(key.Range)}|{NormalizeNumberToken(key.Township)}";
        }

        private static bool TryParsePositiveToken(string token, out int value)
        {
            value = 0;
            var normalized = NormalizeNumberToken(token);
            return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
        }

        private static HashSet<string> BuildContextTownshipKeys(IReadOnlyList<SectionRequest> requests)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (requests == null || requests.Count == 0)
            {
                return keys;
            }

            foreach (var request in requests)
            {
                if (!TryParsePositiveToken(request.Key.Range, out var rangeNum) ||
                    !TryParsePositiveToken(request.Key.Township, out var townshipNum))
                {
                    keys.Add(BuildTownshipKey(request.Key));
                    continue;
                }

                void AddTownship(int rangeCandidate, int townshipCandidate)
                {
                    if (rangeCandidate <= 0 || townshipCandidate <= 0)
                    {
                        return;
                    }

                    var key = new SectionKey(
                        request.Key.Zone,
                        "1",
                        townshipCandidate.ToString(CultureInfo.InvariantCulture),
                        rangeCandidate.ToString(CultureInfo.InvariantCulture),
                        request.Key.Meridian);
                    keys.Add(BuildTownshipKey(key));
                }

                // Always include the request's own township.
                AddTownship(rangeNum, townshipNum);

                var sectionNumber = ParseSectionNumber(request.Key.Section);
                if (!TryGetAtsSectionGridPosition(sectionNumber, out var row, out var col))
                {
                    // Unknown section token: preserve previous behavior (3x3 expansion).
                    for (var dRange = -1; dRange <= 1; dRange++)
                    {
                        for (var dTownship = -1; dTownship <= 1; dTownship++)
                        {
                            AddTownship(rangeNum + dRange, townshipNum + dTownship);
                        }
                    }
                    continue;
                }

                // Only expand to adjacent townships when a requested section touches a township edge.
                // Range increases westward in ATS, so west neighbor is range+1 and east neighbor is range-1.
                var touchesWest = col == 0;
                var touchesEast = col == 5;
                var touchesNorth = row == 0;
                var touchesSouth = row == 5;

                if (touchesWest) AddTownship(rangeNum + 1, townshipNum);
                if (touchesEast) AddTownship(rangeNum - 1, townshipNum);
                if (touchesNorth) AddTownship(rangeNum, townshipNum + 1);
                if (touchesSouth) AddTownship(rangeNum, townshipNum - 1);

                if (touchesWest && touchesNorth) AddTownship(rangeNum + 1, townshipNum + 1);
                if (touchesWest && touchesSouth) AddTownship(rangeNum + 1, townshipNum - 1);
                if (touchesEast && touchesNorth) AddTownship(rangeNum - 1, townshipNum + 1);
                if (touchesEast && touchesSouth) AddTownship(rangeNum - 1, townshipNum - 1);
                }

            return keys;
        }

        private static bool TryParseTownshipKey(
            string townshipKey,
            out int zone,
            out string meridian,
            out string range,
            out string township)
        {
            zone = 0;
            meridian = string.Empty;
            range = string.Empty;
            township = string.Empty;

            if (string.IsNullOrWhiteSpace(townshipKey))
            {
                return false;
            }

            var parts = townshipKey.Split('|');
            if (parts.Length != 4)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out zone))
            {
                return false;
            }

            meridian = parts[1];
            range = parts[2];
            township = parts[3];
            return !string.IsNullOrWhiteSpace(meridian) &&
                   !string.IsNullOrWhiteSpace(range) &&
                   !string.IsNullOrWhiteSpace(township);
        }

        private static bool TryParseSectionNumberToken(string section, out int sectionNumber)
        {
            sectionNumber = 0;
            if (string.IsNullOrWhiteSpace(section))
            {
                return false;
            }

            var raw = section.Trim();
            var match = Regex.Match(raw, "\\d+");
            if (!match.Success)
            {
                return false;
            }

            return int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sectionNumber);
        }

        private static bool IsUsecSeSouthSection(string section)
        {
            if (!TryParseSectionNumberToken(section, out var n))
            {
                return false;
            }

            return (n >= 1 && n <= 6) ||
                   (n >= 13 && n <= 18) ||
                   (n >= 25 && n <= 30);
        }

        private static bool IsUsecBlindSouthSection(string section)
        {
            if (!TryParseSectionNumberToken(section, out var n))
            {
                return false;
            }

            return (n >= 7 && n <= 12) ||
                   (n >= 19 && n <= 24) ||
                   (n >= 31 && n <= 36);
        }

        private static Vector2d GetUnitVector(Point2d from, Point2d to, Vector2d fallback)
        {
            var v = to - from;
            if (v.Length <= 1e-9)
            {
                return fallback;
            }

            return v / v.Length;
        }

        private static Point2d OffsetPoint(Point2d point, Vector2d directionUnit, double distance)
        {
            return point + (directionUnit * distance);
        }

        private static Point2d Midpoint(Point2d a, Point2d b)
        {
            return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        }

        private static Point2d GetTrimmedNorthMidpoint(
            Polyline quarter,
            Vector2d eastUnit,
            Vector2d northUnit,
            double trimDistance,
            Point2d fallback)
        {
            if (!TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                !TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne))
            {
                return fallback;
            }

            var edge = ne - nw;
            if (edge.Length <= (2.0 * trimDistance) + 1e-6)
            {
                return fallback;
            }

            var edgeUnit = edge / edge.Length;
            var start = nw + (edgeUnit * trimDistance);
            var end = ne - (edgeUnit * trimDistance);
            return new Point2d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5);
        }

        private enum QuarterCorner
        {
            NorthWest,
            NorthEast,
            SouthWest,
            SouthEast
        }

        private static bool TryGetQuarterCorner(
            Polyline quarter,
            Vector2d eastUnit,
            Vector2d northUnit,
            QuarterCorner corner,
            out Point2d point)
        {
            point = default;
            if (quarter == null || quarter.NumberOfVertices <= 0)
            {
                return false;
            }

            double bestScore = double.MinValue;
            bool found = false;

            for (var i = 0; i < quarter.NumberOfVertices; i++)
            {
                var p = quarter.GetPoint2dAt(i);
                var e = (p.X * eastUnit.X) + (p.Y * eastUnit.Y);
                var n = (p.X * northUnit.X) + (p.Y * northUnit.Y);

                double score;
                switch (corner)
                {
                    case QuarterCorner.NorthWest:
                        score = n - e;
                        break;
                    case QuarterCorner.NorthEast:
                        score = n + e;
                        break;
                    case QuarterCorner.SouthWest:
                        score = -n - e;
                        break;
                    case QuarterCorner.SouthEast:
                        score = -n + e;
                        break;
                    default:
                        score = double.MinValue;
                        break;
                }

                if (!found || score > bestScore)
                {
                    bestScore = score;
                    point = p;
                    found = true;
                }
            }

            return found;
        }

        private static QuarterSelection PromptForQuarter(Editor editor)
        {
            var options = new PromptKeywordOptions("Select quarter")
            {
                AllowNone = false
            };
            options.Keywords.Add("NW");
            options.Keywords.Add("NE");
            options.Keywords.Add("SW");
            options.Keywords.Add("SE");
            options.Keywords.Add("N");
            options.Keywords.Add("S");
            options.Keywords.Add("E");
            options.Keywords.Add("W");
            options.Keywords.Add("ALL");

            var result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK)
            {
                return QuarterSelection.None;
            }

            switch (result.StringResult.ToUpperInvariant())
            {
                case "NW":
                    return QuarterSelection.NorthWest;
                case "NE":
                    return QuarterSelection.NorthEast;
                case "SW":
                    return QuarterSelection.SouthWest;
                case "SE":
                    return QuarterSelection.SouthEast;
                case "N":
                    return QuarterSelection.NorthHalf;
                case "S":
                    return QuarterSelection.SouthHalf;
                case "E":
                    return QuarterSelection.EastHalf;
                case "W":
                    return QuarterSelection.WestHalf;
                case "ALL":
                    return QuarterSelection.All;
                default:
                    return QuarterSelection.None;
            }
        }

        private static bool TryPromptString(Editor editor, string message, out string value)
        {
            value = string.Empty;
            var options = new PromptStringOptions(message + ": ")
            {
                AllowSpaces = true
            };

            var result = editor.GetString(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return false;
            }

            value = result.StringResult;
            return true;
        }

        private static bool TryPromptInt(Editor editor, string message, out int value)
        {
            value = 0;
            var options = new PromptIntegerOptions(message + ": ")
            {
                AllowNone = false
            };

            var result = editor.GetInteger(options);
            if (result.Status != PromptStatus.OK)
            {
                return false;
            }

            value = result.Value;
            return true;
        }

        private static bool TryBuildQuarterMap(Polyline section, out Dictionary<QuarterSelection, Polyline> quarterMap, out QuarterAnchors anchors)
        {
            quarterMap = new Dictionary<QuarterSelection, Polyline>();

            if (!TryGetQuarterAnchors(section, out anchors))
            {
                anchors = GetFallbackAnchors(section);
            }

            var outline = GetPolylinePoints(section);
            if (outline.Count < 3)
            {
                return false;
            }

            if (TryBuildQuarterPolylines(outline, anchors, out quarterMap))
            {
                return true;
            }

            quarterMap = GenerateQuarterMapFromExtents(section);
            return quarterMap.Count > 0;
        }

        private static bool TryBuildQuarterPolylines(
            IReadOnlyList<Point2d> outline,
            QuarterAnchors anchors,
            out Dictionary<QuarterSelection, Polyline> quarterMap)
        {
            quarterMap = new Dictionary<QuarterSelection, Polyline>();

            var northLineStart = anchors.Left;
            var northLineEnd = anchors.Right;
            var westLineStart = anchors.Top;
            var westLineEnd = anchors.Bottom;

            var northSign = GetSideSign(northLineStart, northLineEnd, anchors.Top);
            if (northSign == 0)
            {
                northSign = GetSideSign(northLineStart, northLineEnd, anchors.Bottom);
            }

            var westSign = GetSideSign(westLineStart, westLineEnd, anchors.Left);
            if (westSign == 0)
            {
                westSign = GetSideSign(westLineStart, westLineEnd, anchors.Right);
            }

            if (northSign == 0 || westSign == 0)
            {
                return false;
            }

            var north = ClipPolygon(outline, northLineStart, northLineEnd, northSign);
            var south = ClipPolygon(outline, northLineStart, northLineEnd, -northSign);
            var northwest = ClipPolygon(north, westLineStart, westLineEnd, westSign);
            var northeast = ClipPolygon(north, westLineStart, westLineEnd, -westSign);
            var southwest = ClipPolygon(south, westLineStart, westLineEnd, westSign);
            var southeast = ClipPolygon(south, westLineStart, westLineEnd, -westSign);

            if (!TryAddQuarter(quarterMap, QuarterSelection.NorthWest, northwest) ||
                !TryAddQuarter(quarterMap, QuarterSelection.NorthEast, northeast) ||
                !TryAddQuarter(quarterMap, QuarterSelection.SouthWest, southwest) ||
                !TryAddQuarter(quarterMap, QuarterSelection.SouthEast, southeast))
            {
                return false;
            }

            return true;
        }

        private static bool TryAddQuarter(Dictionary<QuarterSelection, Polyline> quarterMap, QuarterSelection selection, List<Point2d> points)
        {
            var polyline = BuildPolylineFromPoints(points);
            if (polyline == null)
            {
                return false;
            }

            quarterMap[selection] = polyline;
            return true;
        }

        private static Dictionary<QuarterSelection, Polyline> GenerateQuarterMapFromExtents(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            return new Dictionary<QuarterSelection, Polyline>
            {
                { QuarterSelection.SouthWest, CreateRectangle(minX, minY, midX, midY) },
                { QuarterSelection.SouthEast, CreateRectangle(midX, minY, maxX, midY) },
                { QuarterSelection.NorthWest, CreateRectangle(minX, midY, midX, maxY) },
                { QuarterSelection.NorthEast, CreateRectangle(midX, midY, maxX, maxY) }
            };
        }

        private static QuarterAnchors GetFallbackAnchors(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            return new QuarterAnchors(
                new Point2d(midX, maxY),
                new Point2d(midX, minY),
                new Point2d(minX, midY),
                new Point2d(maxX, midY));
        }

        private static List<Point2d> GetPolylinePoints(Polyline section)
        {
            var points = new List<Point2d>(section.NumberOfVertices);
            for (var i = 0; i < section.NumberOfVertices; i++)
            {
                points.Add(section.GetPoint2dAt(i));
            }

            return points;
        }

        private static double GetSideSign(Point2d lineStart, Point2d lineEnd, Point2d point)
        {
            var lineDir = lineEnd - lineStart;
            var toPoint = point - lineStart;
            var cross = lineDir.X * toPoint.Y - lineDir.Y * toPoint.X;
            if (Math.Abs(cross) < 1e-9)
            {
                return 0;
            }

            return Math.Sign(cross);
        }

        private static List<Point2d> ClipPolygon(IReadOnlyList<Point2d> polygon, Point2d lineStart, Point2d lineEnd, double keepSign)
        {
            var output = new List<Point2d>();
            if (polygon.Count == 0)
            {
                return output;
            }

            var prev = polygon[polygon.Count - 1];
            var prevSide = SignedSide(lineStart, lineEnd, prev);
            var prevInside = IsInside(prevSide, keepSign);

            foreach (var current in polygon)
            {
                var currentSide = SignedSide(lineStart, lineEnd, current);
                var currentInside = IsInside(currentSide, keepSign);

                if (currentInside)
                {
                    if (!prevInside)
                    {
                        if (TryIntersectSegmentWithLine(prev, current, lineStart, lineEnd, out var intersection))
                        {
                            output.Add(intersection);
                        }
                    }

                    output.Add(current);
                }
                else if (prevInside)
                {
                    if (TryIntersectSegmentWithLine(prev, current, lineStart, lineEnd, out var intersection))
                    {
                        output.Add(intersection);
                    }
                }

                prev = current;
                prevSide = currentSide;
                prevInside = currentInside;
            }

            return output;
        }

        private static bool IsInside(double side, double keepSign)
        {
            if (keepSign > 0)
            {
                return side >= -1e-9;
            }

            return side <= 1e-9;
        }

        private static double SignedSide(Point2d lineStart, Point2d lineEnd, Point2d point)
        {
            var lineDir = lineEnd - lineStart;
            var toPoint = point - lineStart;
            return lineDir.X * toPoint.Y - lineDir.Y * toPoint.X;
        }

        private static bool TryIntersectSegmentWithLine(
            Point2d segmentStart,
            Point2d segmentEnd,
            Point2d lineStart,
            Point2d lineEnd,
            out Point2d intersection)
        {
            intersection = default;
            var p = segmentStart;
            var r = segmentEnd - segmentStart;
            var q = lineStart;
            var s = lineEnd - lineStart;

            var cross = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(cross) < 1e-9)
            {
                return false;
            }

            var qmp = q - p;
            var t = (qmp.X * s.Y - qmp.Y * s.X) / cross;
            intersection = new Point2d(p.X + t * r.X, p.Y + t * r.Y);
            return true;
        }

        private static Polyline? BuildPolylineFromPoints(List<Point2d> points)
        {
            if (points.Count < 3)
            {
                return null;
            }

            var cleaned = new List<Point2d>();
            foreach (var point in points)
            {
                if (cleaned.Count == 0 || point.GetDistanceTo(cleaned[cleaned.Count - 1]) > 1e-6)
                {
                    cleaned.Add(point);
                }
            }

            if (cleaned.Count < 3)
            {
                return null;
            }

            if (cleaned[0].GetDistanceTo(cleaned[cleaned.Count - 1]) < 1e-6)
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }

            var polyline = new Polyline(cleaned.Count)
            {
                Closed = true
            };

            for (var i = 0; i < cleaned.Count; i++)
            {
                polyline.AddVertexAt(i, cleaned[i], 0, 0, 0);
            }

            return polyline;
        }

        private static bool TryGetQuarterAnchors(Polyline section, out QuarterAnchors anchors)
        {
            anchors = default;
            if (section.NumberOfVertices < 3)
            {
                return false;
            }

            var vertices = new List<Point3d>(section.NumberOfVertices);
            for (var i = 0; i < section.NumberOfVertices; i++)
            {
                var point = section.GetPoint2dAt(i);
                vertices.Add(new Point3d(point.X, point.Y, 0));
            }

            if (!TryGetQuarterAnchorsByEdgeMedianVertexChain(vertices, out var topV, out var bottomV, out var leftV, out var rightV))
            {
                return false;
            }

            anchors = new QuarterAnchors(
                new Point2d(topV.X, topV.Y),
                new Point2d(bottomV.X, bottomV.Y),
                new Point2d(leftV.X, leftV.Y),
                new Point2d(rightV.X, rightV.Y));
            return true;
        }

        private static bool TryGetQuarterAnchorsByEdgeMedianVertexChain(
            List<Point3d> verts,
            out Point3d topV,
            out Point3d bottomV,
            out Point3d leftV,
            out Point3d rightV)
        {
            topV = bottomV = leftV = rightV = Point3d.Origin;
            if (verts == null || verts.Count < 3)
            {
                return false;
            }

            var n = verts.Count;
            var edges = new List<EdgeInfo>(n);
            for (var i = 0; i < n; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % n];
                var v = b - a;
                var len = v.Length;
                if (len <= 1e-9)
                {
                    continue;
                }

                var u = new Vector3d(v.X / len, v.Y / len, 0);
                var mid = new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                edges.Add(new EdgeInfo { Index = i, A = a, B = b, U = u, Mid = mid, Len = len });
            }

            if (edges.Count == 0)
            {
                return false;
            }

            const double degTol = 12.0;
            var cosTol = Math.Cos(degTol * Math.PI / 180.0);
            var topEdge = default(EdgeInfo);
            var bestTopY = double.MinValue;
            foreach (var edge in edges)
            {
                var horiz = Math.Abs(edge.U.DotProduct(Vector3d.XAxis));
                var avgY = (edge.A.Y + edge.B.Y) * 0.5;
                if (horiz >= cosTol && avgY > bestTopY)
                {
                    bestTopY = avgY;
                    topEdge = edge;
                }
            }

            if (bestTopY == double.MinValue)
            {
                topEdge = edges.OrderByDescending(edge => edge.Len).First();
            }

            var east = topEdge.U.GetNormal();
            if (east.Length <= 1e-12)
            {
                return false;
            }

            var north = east.RotateBy(Math.PI / 2.0, Vector3d.ZAxis).GetNormal();

            var minE = double.MaxValue;
            var maxE = double.MinValue;
            var minN = double.MaxValue;
            var maxN = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                var dp = verts[i] - Point3d.Origin;
                var pe = dp.DotProduct(east);
                var pn = dp.DotProduct(north);
                minE = Math.Min(minE, pe);
                maxE = Math.Max(maxE, pe);
                minN = Math.Min(minN, pn);
                maxN = Math.Max(maxN, pn);
            }

            var spanE = Math.Max(1e-6, maxE - minE);
            var spanN = Math.Max(1e-6, maxN - minN);
            var bandTol = Math.Max(5.0, 0.01 * Math.Max(spanE, spanN));

            var eChains = BuildChainsClosest(edges, east, north);
            var nChains = BuildChainsClosest(edges, north, east);
            if (eChains.Count == 0 || nChains.Count == 0)
            {
                return false;
            }

            bool TouchesTop(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (maxN - AxisProj(verts[idx], north) <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TouchesBottom(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (AxisProj(verts[idx], north) - minN <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TouchesLeft(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (AxisProj(verts[idx], east) - minE <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TouchesRight(ChainInfo ch)
            {
                foreach (var idx in ChainVertexIndices(ch, n))
                {
                    if (maxE - AxisProj(verts[idx], east) <= bandTol)
                    {
                        return true;
                    }
                }

                return false;
            }

            var top = eChains.Where(TouchesTop).DefaultIfEmpty(eChains.OrderByDescending(c => c.Score).First()).First();
            var bottom = eChains.Where(TouchesBottom).DefaultIfEmpty(eChains.OrderBy(c => c.Score).First()).First();
            var left = nChains.Where(TouchesLeft).DefaultIfEmpty(nChains.OrderBy(c => c.Score).First()).First();
            var right = nChains.Where(TouchesRight).DefaultIfEmpty(nChains.OrderByDescending(c => c.Score).First()).First();

            var targetE = 0.5 * (minE + maxE);
            topV = ChainVertexNearestAxisValue(verts, top, east, targetE);
            bottomV = ChainVertexNearestAxisValue(verts, bottom, east, targetE);

            var targetN = 0.5 * (minN + maxN);
            leftV = ChainVertexNearestAxisValue(verts, left, north, targetN);
            rightV = ChainVertexNearestAxisValue(verts, right, north, targetN);

            var Emid = 0.5 * (AxisProj(leftV, east) + AxisProj(rightV, east));
            var Nmid = 0.5 * (AxisProj(topV, north) + AxisProj(bottomV, north));
            if (Math.Abs(Emid - 0.5 * (minE + maxE)) > 0.25 * spanE ||
                Math.Abs(Nmid - 0.5 * (minN + maxN)) > 0.25 * spanN)
            {
                Point3d FromEN(double e, double nCoord) =>
                    new Point3d(east.X * e + north.X * nCoord, east.Y * e + north.Y * nCoord, 0);

                topV = FromEN(0.5 * (minE + maxE), maxN);
                bottomV = FromEN(0.5 * (minE + maxE), minN);
                leftV = FromEN(minE, 0.5 * (minN + maxN));
                rightV = FromEN(maxE, 0.5 * (minN + maxN));
            }

            return true;
        }

        private static List<ChainInfo> BuildChainsClosest(List<EdgeInfo> edges, Vector3d primary, Vector3d other)
        {
            var chains = new List<ChainInfo>();
            var inChain = false;
            var start = -1;
            var sumProj = 0.0;
            var cnt = 0;
            var totLen = 0.0;

            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                var de = Math.Abs(edge.U.DotProduct(primary));
                var dn = Math.Abs(edge.U.DotProduct(other));
                var isPrimary = de >= dn;

                if (isPrimary)
                {
                    if (!inChain)
                    {
                        inChain = true;
                        start = edge.Index;
                        sumProj = 0.0;
                        cnt = 0;
                        totLen = 0.0;
                    }

                    sumProj += (edge.Mid - Point3d.Origin).DotProduct(other);
                    cnt++;
                    totLen += edge.Len;
                }
                else if (inChain)
                {
                    chains.Add(new ChainInfo
                    {
                        Start = start,
                        SegCount = cnt,
                        Score = cnt > 0 ? sumProj / cnt : 0.0,
                        TotalLen = totLen
                    });
                    inChain = false;
                }
            }

            if (inChain)
            {
                chains.Add(new ChainInfo
                {
                    Start = start,
                    SegCount = cnt,
                    Score = cnt > 0 ? sumProj / cnt : 0.0,
                    TotalLen = totLen
                });
            }

            if (chains.Count >= 2)
            {
                var first = chains[0];
                var last = chains[chains.Count - 1];
                if (first.Start == 0 && (last.Start + last.SegCount == edges.Count))
                {
                    var totalSeg = last.SegCount + first.SegCount;
                    var totalLen = last.TotalLen + first.TotalLen;
                    var avgScore = totalSeg > 0
                        ? (last.Score * last.SegCount + first.Score * first.SegCount) / totalSeg
                        : 0.0;
                    var merged = new ChainInfo
                    {
                        Start = last.Start,
                        SegCount = totalSeg,
                        Score = avgScore,
                        TotalLen = totalLen
                    };
                    chains[0] = merged;
                    chains.RemoveAt(chains.Count - 1);
                }
            }

            return chains;
        }

        private static IEnumerable<int> ChainVertexIndices(ChainInfo chain, int vertexCount)
        {
            for (var k = 0; k <= chain.SegCount; k++)
            {
                yield return (chain.Start + k) % vertexCount;
            }
        }

        private static double AxisProj(Point3d point, Vector3d axis)
        {
            return (point - Point3d.Origin).DotProduct(axis);
        }

        private static Point3d ChainVertexNearestAxisValue(List<Point3d> verts, ChainInfo chain, Vector3d axis, double target)
        {
            var bestIdx = chain.Start % verts.Count;
            var best = double.MaxValue;

            foreach (var idx in ChainVertexIndices(chain, verts.Count))
            {
                var distance = Math.Abs(AxisProj(verts[idx], axis) - target);
                if (distance < best)
                {
                    best = distance;
                    bestIdx = idx;
                }
            }

            return verts[bestIdx];
        }

    }

    public sealed class QuarterLabelInfo
    {
        public QuarterLabelInfo(
            ObjectId quarterId,
            SectionKey sectionKey,
            QuarterSelection quarter,
            string secType = "L-USEC",
            ObjectId sectionPolylineId = default)
        {
            QuarterId = quarterId;
            SectionKey = sectionKey;
            Quarter = quarter;
            SecType = string.Equals(secType?.Trim(), "L-SEC", StringComparison.OrdinalIgnoreCase)
                ? "L-SEC"
                : "L-USEC";
            SectionPolylineId = sectionPolylineId;
        }

        public ObjectId QuarterId { get; }
        public SectionKey SectionKey { get; }
        public QuarterSelection Quarter { get; }
        public string SecType { get; }
        public ObjectId SectionPolylineId { get; }
    }

    public sealed class SectionDrawResult
    {
        public SectionDrawResult(
            List<ObjectId> labelQuarterPolylineIds,
            List<QuarterLabelInfo> labelQuarterInfos,
            List<ObjectId> quarterPolylineIds,
            List<ObjectId> quarterHelperEntityIds,
            List<ObjectId> sectionPolylineIds,
            List<ObjectId> contextSectionPolylineIds,
            List<ObjectId> sectionLabelEntityIds,
            Dictionary<ObjectId, int> sectionNumberByPolylineId,
            bool generatedFromIndex)
        {
            LabelQuarterPolylineIds = labelQuarterPolylineIds ?? new List<ObjectId>();
            LabelQuarterInfos = labelQuarterInfos ?? new List<QuarterLabelInfo>();
            QuarterPolylineIds = quarterPolylineIds ?? new List<ObjectId>();
            QuarterHelperEntityIds = quarterHelperEntityIds ?? new List<ObjectId>();
            SectionPolylineIds = sectionPolylineIds ?? new List<ObjectId>();
            ContextSectionPolylineIds = contextSectionPolylineIds ?? new List<ObjectId>();
            SectionLabelEntityIds = sectionLabelEntityIds ?? new List<ObjectId>();
            SectionNumberByPolylineId = sectionNumberByPolylineId ?? new Dictionary<ObjectId, int>();
            GeneratedFromIndex = generatedFromIndex;
        }
        public List<ObjectId> LabelQuarterPolylineIds { get; }
        public List<QuarterLabelInfo> LabelQuarterInfos { get; }
        public List<ObjectId> QuarterPolylineIds { get; }
        public List<ObjectId> QuarterHelperEntityIds { get; }
        public List<ObjectId> SectionPolylineIds { get; }
        public List<ObjectId> ContextSectionPolylineIds { get; }
        public List<ObjectId> SectionLabelEntityIds { get; }
        public Dictionary<ObjectId, int> SectionNumberByPolylineId { get; }
        public bool GeneratedFromIndex { get; }
    }

    public sealed class LsdCellInfo
    {
        public LsdCellInfo(Polyline cell, int lsd, int section)
        {
            Cell = cell;
            Lsd = lsd;
            Section = section;
        }

        public Polyline Cell { get; }
        public int Lsd { get; }
        public int Section { get; }
    }

    public sealed class SectionSpatialInfo
    {
        public SectionSpatialInfo(
            Polyline sectionPolyline,
            int section,
            Point2d southWest,
            Vector2d eastUnit,
            Vector2d northUnit,
            double width,
            double height)
        {
            SectionPolyline = sectionPolyline;
            Section = section;
            SouthWest = southWest;
            EastUnit = eastUnit;
            NorthUnit = northUnit;
            Width = width;
            Height = height;
        }

        public Polyline SectionPolyline { get; }
        public int Section { get; }
        public Point2d SouthWest { get; }
        public Vector2d EastUnit { get; }
        public Vector2d NorthUnit { get; }
        public double Width { get; }
        public double Height { get; }
    }

    public sealed class SectionBuildResult
    {
        public SectionBuildResult(
            ObjectId sectionPolylineId,
            Dictionary<QuarterSelection, ObjectId> quarterPolylineIds,
            List<ObjectId> quarterHelperEntityIds,
            ObjectId sectionLabelEntityId)
        {
            SectionPolylineId = sectionPolylineId;
            QuarterPolylineIds = quarterPolylineIds ?? new Dictionary<QuarterSelection, ObjectId>();
            QuarterHelperEntityIds = quarterHelperEntityIds ?? new List<ObjectId>();
            SectionLabelEntityId = sectionLabelEntityId;
        }

        public ObjectId SectionPolylineId { get; }
        public Dictionary<QuarterSelection, ObjectId> QuarterPolylineIds { get; }
        public List<ObjectId> QuarterHelperEntityIds { get; }
        public ObjectId SectionLabelEntityId { get; }
    }

    public sealed class SectionRequest
    {
        public SectionRequest(QuarterSelection quarter, SectionKey key, string secType = "AUTO")
        {
            Quarter = quarter;
            Key = key;
            SecType = string.IsNullOrWhiteSpace(secType) ? "AUTO" : secType.Trim().ToUpperInvariant();
        }

        public QuarterSelection Quarter { get; }
        public SectionKey Key { get; }
        public string SecType { get; }
    }

    public enum QuarterSelection
    {
        None,
        NorthWest,
        NorthEast,
        SouthWest,
        SouthEast,
        NorthHalf,
        SouthHalf,
        EastHalf,
        WestHalf,
        All
    }

    public sealed class SummaryResult
    {
        public int TotalDispositions { get; set; }
        public int LabelsPlaced { get; set; }
        public int SkippedNoOd { get; set; }
        public int SkippedNotClosed { get; set; }
        public int SkippedNoLayerMapping { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
        public int ImportedDispositions { get; set; }
        public int FilteredDispositions { get; set; }
        public int DedupedDispositions { get; set; }
        public int ImportFailures { get; set; }
    }

    public sealed class Logger : IDisposable
    {
        private StreamWriter? _writer;

        public void Initialize(string path)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                WriteLine("---- ATSBUILD " + DateTime.Now + " ----");
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Logger init failed for {path}: {ex.Message}");
                var fallbackPath = BuildFallbackLogPath(path);
                try
                {
                    var stream = new FileStream(fallbackPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream) { AutoFlush = true };
                    WriteLine("---- ATSBUILD " + DateTime.Now + " ----");
                    WriteLine($"Logger initialized with fallback path: {fallbackPath}");
                }
                catch (IOException fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Logger fallback init failed for {fallbackPath}: {fallbackEx.Message}");
                }
            }
        }

        public void WriteLine(string message)
        {
            _writer?.WriteLine(message);
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }

        private static string BuildFallbackLogPath(string path)
        {
            var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            return Path.Combine(directory, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
        }
    }
}

/////////////////////////////////////////////////////////////////////










