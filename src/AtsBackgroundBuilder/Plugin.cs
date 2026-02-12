/////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ImportExport;

namespace AtsBackgroundBuilder
{
    public class Plugin : IExtensionApplication
    {
        private const string RaDiagBuildTag = "RA-DIAG-BUILD 2026-02-08-alt6";
        private const double RoadAllowanceUsecWidthMeters = 30.17;
        private const double RoadAllowanceSecWidthMeters = 20.11;
        private const double RoadAllowanceWidthToleranceMeters = 0.10;
        private const double RoadAllowanceGapOffsetToleranceMeters = 0.35;
        private const double MinAdjustableLsdLineLengthMeters = 20.0;
        // Heavy road-allowance tracing is off by default; enable with ATSBUILD_RA_DIAG=1 when needed.
        private static readonly bool EnableRoadAllowanceDiagnostics =
            string.Equals(Environment.GetEnvironmentVariable("ATSBUILD_RA_DIAG"), "1", StringComparison.OrdinalIgnoreCase);
        // 100m DEFPOINTS buffer windows are off by default for performance testing.
        private static readonly bool EnableBufferedQuarterWindowDrawing =
            string.Equals(Environment.GetEnvironmentVariable("ATSBUILD_DRAW_100M_BUFFER"), "1", StringComparison.OrdinalIgnoreCase);
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
                    buildResult = DrawSectionFromIndex(editor, database, outline, request.Key, drawLsds, resolvedSecType, resolvedQuarterSecTypes);
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

            foreach (var id in DrawAdjoiningSectionsForContext(
                database,
                searchFolders,
                requests,
                labelQuarterIds,
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

            CleanupContextSectionOverlaps(database, contextSectionIds, logger);
            logger.WriteLine($"TIMING DrawSectionsFromRequests: context sections processed in {timer.ElapsedMilliseconds} ms");

            var generatedRoadAllowanceIds = new HashSet<ObjectId>();
            foreach (var id in DrawRoadAllowanceGapOffsetLines(
                database,
                searchFolders,
                requests,
                labelQuarterIds,
                inferredSecTypes,
                inferredQuarterSecTypes,
                logger))
            {
                quarterHelperIds.Add(id);
                generatedRoadAllowanceIds.Add(id);
            }

            var quarterInfosForRoadAllowanceRules = new List<QuarterLabelInfo>(labelQuarterInfos.Count + contextRuleSectionInfos.Count);
            quarterInfosForRoadAllowanceRules.AddRange(labelQuarterInfos);
            quarterInfosForRoadAllowanceRules.AddRange(contextRuleSectionInfos);
            SnapQuarterLsdLinesToSectionBoundaries(database, labelQuarterIds, logger);
            CleanupGeneratedRoadAllowanceOverlaps(database, generatedRoadAllowanceIds, logger);
            ExtendSouthBoundarySwQuarterWestToNextUsec(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            ExtendQuarterLinesFromUsecWestSouthToNextUsec(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            ExtendNwQuarterWestUsecNorthToNextHorizontalUsec(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            ConnectUsecBlindSouthwestTwentyTwelveLines(database, quarterInfosForRoadAllowanceRules, generatedRoadAllowanceIds, logger);
            ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(database, searchFolders, quarterInfosForRoadAllowanceRules, generatedRoadAllowanceIds, logger);
            CleanupDuplicateBlindLineSegments(database, labelQuarterIds, logger);
            TrimContextSectionsToBufferedWindows(database, contextSectionIds, labelQuarterIds, logger);
            SnapContextEndpointsAfterTrim(database, contextSectionIds, labelQuarterIds, logger);
            StitchTrimmedContextSectionEndpoints(database, contextSectionIds, labelQuarterIds, logger);
            HealBufferedBoundaryEndpointSeams(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            CleanupContextSectionOverlaps(database, contextSectionIds, logger);
            NormalizeGeneratedRoadAllowanceLayers(database, generatedRoadAllowanceIds, logger);
            NormalizeShortRoadAllowanceLayersByNeighborhood(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            NormalizeHorizontalSecRoadAllowanceLayers(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            NormalizeBottomTownshipBoundaryLayers(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            NormalizeThirtyEighteenCorridorLayers(database, labelQuarterIds, logger);
            // TODO: unresolved WIP - R/A layer mix/match still occurs on corridors perpendicular
            // to township/range switch boundaries. Keep disabled range-edge relayer until deterministic fix.
            CleanupDuplicateBlindLineSegments(database, labelQuarterIds, logger);
            // Final SW 20.12 corner pass after duplicate/trim normalization so required joins persist.
            ConnectUsecBlindSouthwestTwentyTwelveLines(database, quarterInfosForRoadAllowanceRules, generatedRoadAllowanceIds, logger);
            CloseTinyRoadAllowanceCornerGaps(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            FinalSnapLsdEndpointsToTwentyTwelveMidpoints(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            CloseTinyRoadAllowanceCornerGaps(database, labelQuarterIds, generatedRoadAllowanceIds, logger);
            RebuildLsdLabelsAtFinalIntersections(database, lsdQuarterInfos, logger);
            logger.WriteLine($"TIMING DrawSectionsFromRequests: road allowances processed in {timer.ElapsedMilliseconds} ms");

            if (EnableBufferedQuarterWindowDrawing)
            {
                DrawBufferedQuarterWindowsOnDefpoints(database, labelQuarterIds, 100.0, logger);
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
            Logger logger)
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

            var selectedSectionKeyIds = new HashSet<string>(
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
                string NwQuarterLayer)>();
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
                            nwType));
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

            var selectedOrLocalKeyIds = localKeyIds.Count > 0
                ? localKeyIds
                : selectedSectionKeyIds;
            if (EnableRoadAllowanceDiagnostics)
            {
                logger?.WriteLine($"RA-DIAG selection: requested={selectedSectionKeyIds.Count}, local={localKeyIds.Count}, active={selectedOrLocalKeyIds.Count}");
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
                (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d LeftMid, Point2d RightMid, Point2d TopMid, Point2d BottomMid, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY, string SouthEdgeLayer, string EastEdgeLayer, string NorthEdgeLayer, string WestEdgeLayer, string SwQuarterLayer, string SeQuarterLayer, string NeQuarterLayer, string NwQuarterLayer) a,
                (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d LeftMid, Point2d RightMid, Point2d TopMid, Point2d BottomMid, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY, string SouthEdgeLayer, string EastEdgeLayer, string NorthEdgeLayer, string WestEdgeLayer, string SwQuarterLayer, string SeQuarterLayer, string NeQuarterLayer, string NwQuarterLayer) b)
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
                logger?.WriteLine($"RA-DIAG summary: candidates={diagCandidates}, eligible={diagEligible}, near30.17={diagMatched30}, near20.11={diagMatched20}, addPreClip={offsetSpecs.Count}");
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
                    var drewAny = false;
                    foreach (var clipWindow in clipWindows)
                    {
                        if (!TryClipSegmentToWindow(seg.A, seg.B, clipWindow, out var a, out var b))
                        {
                            continue;
                        }

                        var key = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                            a.X, a.Y, b.X, b.Y);
                        if (!drawnSegmentKeys.Add(key))
                        {
                            continue;
                        }

                        clippedSegments.Add((a, b, seg.Tag, seg.Layer));
                        drewAny = true;

                        if (EnableRoadAllowanceDiagnostics)
                            logger?.WriteLine($"RA-DIAG DRAW {seg.Tag}: layer={seg.Layer} A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###})");
                    }

                    if (!drewAny && EnableRoadAllowanceDiagnostics)
                    {
                        logger?.WriteLine($"RA-DIAG CLIP-OUT {seg.Tag}");
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
                        // Cross-tag merges can chain 20.12 segments across sections.
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
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
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
                var toEraseExisting = new HashSet<ObjectId>();
                foreach (var g in generatedSegments)
                {
                    if (toEraseGenerated.Contains(g.Id))
                    {
                        continue;
                    }

                    foreach (var e in existingSegments)
                    {
                        if (toEraseExisting.Contains(e.Id))
                        {
                            continue;
                        }

                        if (!AreSegmentsDuplicateOrCollinearOverlap(g.A, g.B, e.A, e.B))
                        {
                            continue;
                        }

                        var existingInsideGenerated = IsSegmentContained(e.A, e.B, g.A, g.B);
                        var generatedInsideExisting = IsSegmentContained(g.A, g.B, e.A, e.B);
                        if (existingInsideGenerated && (g.Length - e.Length) > lengthDeltaTol)
                        {
                            // Prefer freshly generated full corridor over stale/clipped fragments.
                            toEraseExisting.Add(e.Id);
                            continue;
                        }

                        if (generatedInsideExisting && (e.Length - g.Length) > lengthDeltaTol)
                        {
                            // Existing full-length boundary already present; drop redundant generated.
                            toEraseGenerated.Add(g.Id);
                            break;
                        }

                        // Near-equal overlap:
                        // keep existing L-SEC fabric (often authoritative 20.12),
                        // otherwise prefer newly generated geometry.
                        if (string.Equals(e.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                        {
                            toEraseGenerated.Add(g.Id);
                            break;
                        }

                        toEraseExisting.Add(e.Id);
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

                var erasedExisting = 0;
                foreach (var id in toEraseExisting)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Erase();
                    erasedExisting++;
                }

                tr.Commit();
                if (erasedGenerated > 0 || erasedExisting > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: overlap resolve erased {erasedGenerated} generated and {erasedExisting} existing RA segment(s).");
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

        private static void CleanupDuplicateBlindLineSegments(
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, Point2d Mid, double Len)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
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
                    if (len < 2.0 || len > 2000.0)
                    {
                        continue;
                    }

                    if (!IsHorizontalLike(a, b) && !IsVerticalLike(a, b))
                    {
                        continue;
                    }

                    candidates.Add((id, ent.Layer, a, b, Midpoint(a, b), len));
                }

                if (candidates.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                const double endpointTol = 0.75;
                const double midpointTol = 0.60;
                const double lengthTol = 0.60;
                const double minBlindLen = 8.0;
                const double maxBlindLen = 2000.0;
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

                    if (a.Len < minBlindLen || a.Len > maxBlindLen)
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

                        if (b.Len < minBlindLen || b.Len > maxBlindLen)
                        {
                            continue;
                        }

                        var nearDuplicate = AreSegmentEndpointsNear(a.A, a.B, b.A, b.B, endpointTol);
                        if (!nearDuplicate)
                        {
                            var collinearOverlap = AreSegmentsDuplicateOrCollinearOverlap(a.A, a.B, b.A, b.B);
                            if (!collinearOverlap)
                            {
                                continue;
                            }

                            var similarShape =
                                Math.Abs(a.Len - b.Len) <= lengthTol &&
                                a.Mid.GetDistanceTo(b.Mid) <= midpointTol;
                            var contained =
                                IsSegmentContained(a.A, a.B, b.A, b.B) ||
                                IsSegmentContained(b.A, b.B, a.A, a.B);
                            if (!similarShape && !contained)
                            {
                                continue;
                            }
                        }

                        var eraseId = a.Id.Handle.Value > b.Id.Handle.Value ? a.Id : b.Id;
                        var aUsec = string.Equals(a.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        var bUsec = string.Equals(b.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        if (aUsec != bUsec)
                        {
                            // Prefer keeping L-USEC when duplicates were emitted on mixed layers.
                            eraseId = aUsec ? b.Id : a.Id;
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
                    logger?.WriteLine($"Cleanup: erased {erased} duplicate blind-line segment(s) on adjoining sections.");
                }
            }
        }

        private static void NormalizeGeneratedRoadAllowanceLayers(
            Database database,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

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
                const double lengthMin = 4.0;

                var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var generatedSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
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

                    var len = a.GetDistanceTo(b);
                    if (len < lengthMin)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        generatedSegments.Add((id, a, b, horizontal, vertical, len));
                    }
                }

                if (generatedSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var normalizedGenerated = 0;
                for (var i = 0; i < generatedSegments.Count; i++)
                {
                    var seg = generatedSegments[i];
                    if (!(tr.GetObject(seg.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (string.Equals(writable.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(writable.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = "L-USEC";
                    writable.ColorIndex = 256;
                    normalizedGenerated++;
                }

                tr.Commit();
                if (normalizedGenerated > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized {normalizedGenerated} generated RA segment(s) with invalid layer to L-USEC [candidates={generatedSegments.Count}].");
                }
            }
        }

        private static void SnapContextEndpointsAfterTrim(
            Database database,
            IReadOnlyCollection<ObjectId> contextSectionIds,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || contextSectionIds == null || contextSectionIds.Count == 0 || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

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

                var contextSet = new HashSet<ObjectId>(contextSectionIds.Where(id => !id.IsNull));
                var endpointAnchors = new List<(ObjectId Id, bool IsContext, Point2d P)>();
                var nonContextSegments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
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

                    var isContext = contextSet.Contains(id);
                    endpointAnchors.Add((id, isContext, a));
                    endpointAnchors.Add((id, isContext, b));
                    if (!isContext)
                    {
                        nonContextSegments.Add((id, ent.Layer ?? string.Empty, a, b, IsHorizontalLike(a, b), IsVerticalLike(a, b)));
                    }
                }

                if (endpointAnchors.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointSnapTol = 1.20;
                const double segmentSnapTol = 0.60;
                const double moveTol = 0.02;
                var adjusted = 0;
                foreach (var id in contextSet.ToList())
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var p0, out var p1))
                    {
                        continue;
                    }

                    var currentLayer = ent.Layer ?? string.Empty;
                    var thisIsHorizontal = IsHorizontalLike(p0, p1);
                    var thisIsVertical = IsVerticalLike(p0, p1);

                    bool TrySnapEndpoint(Point2d endpoint, Point2d oppositeEndpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDist = double.MaxValue;
                        var bestTargetIsContext = true;
                        var ownerDirVec = oppositeEndpoint - endpoint;
                        var ownerDirLen = ownerDirVec.Length;
                        if (ownerDirLen <= 1e-6)
                        {
                            return false;
                        }

                        var ownerDir = ownerDirVec / ownerDirLen;
                        // Bearing-preserving endpoint adjustment:
                        // project/extend only along the current segment orientation.
                        const double collinearDotMin = 0.995;
                        const double perpendicularDotMax = 0.10;
                        const double collinearOffsetTol = 0.85;

                        for (var i = 0; i < endpointAnchors.Count; i++)
                        {
                            var anchor = endpointAnchors[i];
                            if (anchor.Id == id)
                            {
                                continue;
                            }

                            var t = (anchor.P - endpoint).DotProduct(ownerDir);
                            var candidate = endpoint + (ownerDir * t);
                            var d = endpoint.GetDistanceTo(candidate);
                            if (d > endpointSnapTol)
                            {
                                continue;
                            }

                            var lateral = candidate.GetDistanceTo(anchor.P);
                            if (lateral > collinearOffsetTol)
                            {
                                continue;
                            }

                            var prefer = !anchor.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = anchor.IsContext;
                            snapped = candidate;
                        }

                        for (var i = 0; i < nonContextSegments.Count; i++)
                        {
                            var seg = nonContextSegments[i];
                            if (!string.Equals(seg.Layer, currentLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var otherVec = seg.B - seg.A;
                            var otherLen = otherVec.Length;
                            if (otherLen <= 1e-6)
                            {
                                continue;
                            }

                            var otherDir = otherVec / otherLen;
                            var cosAbs = Math.Abs(ownerDir.DotProduct(otherDir));
                            Point2d candidate;
                            double d;
                            if (cosAbs >= collinearDotMin)
                            {
                                var cA = endpoint + (ownerDir * ((seg.A - endpoint).DotProduct(ownerDir)));
                                var cB = endpoint + (ownerDir * ((seg.B - endpoint).DotProduct(ownerDir)));
                                var dA = endpoint.GetDistanceTo(cA);
                                var dB = endpoint.GetDistanceTo(cB);
                                candidate = dA <= dB ? cA : cB;
                                d = dA <= dB ? dA : dB;
                                var raw = dA <= dB ? seg.A : seg.B;
                                if (candidate.GetDistanceTo(raw) > collinearOffsetTol || d > endpointSnapTol)
                                {
                                    continue;
                                }
                            }
                            else if (cosAbs <= perpendicularDotMax)
                            {
                                if (!TryIntersectInfiniteLineWithSegment(endpoint, ownerDir, seg.A, seg.B, out var tCross))
                                {
                                    continue;
                                }

                                candidate = endpoint + (ownerDir * tCross);
                                d = Math.Abs(tCross);
                                if (d > segmentSnapTol)
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                continue;
                            }

                            if (!found || d < (bestDist - 1e-9))
                            {
                                found = true;
                                bestDist = d;
                                bestTargetIsContext = false;
                                snapped = candidate;
                            }
                        }

                        return found;
                    }

                    var new0 = p0;
                    var new1 = p1;
                    TrySnapEndpoint(p0, p1, out new0);
                    TrySnapEndpoint(p1, p0, out new1);

                    if (new0.GetDistanceTo(p0) <= moveTol && new1.GetDistanceTo(p1) <= moveTol)
                    {
                        continue;
                    }

                    if (TryWriteOpenSegment(ent, new0, new1))
                    {
                        adjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: snapped {adjusted} context segment endpoint(s) after 100m trim.");
                }
            }
        }

        private static void HealBufferedBoundaryEndpointSeams(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId>? generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            var generatedSet = generatedRoadAllowanceIds != null && generatedRoadAllowanceIds.Count > 0
                ? new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull))
                : new HashSet<ObjectId>();

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
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

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

                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id))
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

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointSnapTol = 1.60;
                const double crossSnapTol = 1.60;
                const double moveTol = 0.01;
                var adjusted = 0;
                for (var si = 0; si < segments.Count; si++)
                {
                    var seg = segments[si];
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(seg.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var p0, out var p1))
                    {
                        continue;
                    }

                    var isHorizontal = IsHorizontalLike(p0, p1);
                    var isVertical = IsVerticalLike(p0, p1);
                    if (!isHorizontal && !isVertical)
                    {
                        continue;
                    }

                    bool TrySnapEndpoint(Point2d endpoint, Point2d oppositeEndpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDistance = double.MaxValue;
                        var bestSameLayer = false;
                        var ownerDirVec = oppositeEndpoint - endpoint;
                        var ownerDirLen = ownerDirVec.Length;
                        if (ownerDirLen <= 1e-6)
                        {
                            return false;
                        }

                        var ownerDir = ownerDirVec / ownerDirLen;

                        // Bearing-preserving seam cleanup only:
                        // - collinear projection along owner segment
                        // - perpendicular intersection along owner segment
                        const double collinearDotMin = 0.995;
                        const double perpendicularDotMax = 0.10;
                        const double collinearOffsetTol = 0.85;
                        for (var oi = 0; oi < segments.Count; oi++)
                        {
                            if (oi == si)
                            {
                                continue;
                            }

                            var other = segments[oi];
                            var sameLayer = string.Equals(seg.Layer, other.Layer, StringComparison.OrdinalIgnoreCase);
                            var otherVec = other.B - other.A;
                            var otherLen = otherVec.Length;
                            if (otherLen <= 1e-6)
                            {
                                continue;
                            }

                            var otherDir = otherVec / otherLen;
                            var cosAbs = Math.Abs(ownerDir.DotProduct(otherDir));

                            if (cosAbs >= collinearDotMin)
                            {
                                var colCandidates = new[] { other.A, other.B };
                                for (var ci = 0; ci < colCandidates.Length; ci++)
                                {
                                    var c = colCandidates[ci];
                                    var lateral = DistancePointToInfiniteLine(c, endpoint, endpoint + ownerDir);
                                    if (lateral > collinearOffsetTol)
                                    {
                                        continue;
                                    }

                                    var t = (c - endpoint).DotProduct(ownerDir);
                                    var d = Math.Abs(t);
                                    if (d > endpointSnapTol)
                                    {
                                        continue;
                                    }

                                    var candidate = endpoint + (ownerDir * t);
                                    if (!found ||
                                        (sameLayer && !bestSameLayer) ||
                                        (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                    {
                                        found = true;
                                        bestDistance = d;
                                        bestSameLayer = sameLayer;
                                        snapped = candidate;
                                    }
                                }
                            }

                            if (cosAbs <= perpendicularDotMax)
                            {
                                if (!TryIntersectInfiniteLineWithSegment(endpoint, ownerDir, other.A, other.B, out var t))
                                {
                                    continue;
                                }

                                var d = Math.Abs(t);
                                if (d > crossSnapTol)
                                {
                                    continue;
                                }

                                var candidate = endpoint + (ownerDir * t);
                                if (!found ||
                                    (sameLayer && !bestSameLayer) ||
                                    (sameLayer == bestSameLayer && d < (bestDistance - 1e-9)))
                                {
                                    found = true;
                                    bestDistance = d;
                                    bestSameLayer = sameLayer;
                                    snapped = candidate;
                                }
                            }
                        }

                        return found;
                    }

                    var new0 = p0;
                    var new1 = p1;
                    TrySnapEndpoint(p0, p1, out new0);
                    TrySnapEndpoint(p1, p0, out new1);

                    if (new0.GetDistanceTo(p0) <= moveTol && new1.GetDistanceTo(p1) <= moveTol)
                    {
                        continue;
                    }

                    if (!TryWriteOpenSegment(ent, new0, new1))
                    {
                        continue;
                    }

                    adjusted++;
                    segments[si] = (seg.Id, seg.Layer, new0, new1, IsHorizontalLike(new0, new1), IsVerticalLike(new0, new1));
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: seam-healed {adjusted} L-SEC/L-USEC segment(s) near buffered boundary endpoints.");
            }
        }

        private static void CloseTinyRoadAllowanceCornerGaps(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId>? generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            var generatedSet = generatedRoadAllowanceIds != null && generatedRoadAllowanceIds.Count > 0
                ? new HashSet<ObjectId>(generatedRoadAllowanceIds)
                : new HashSet<ObjectId>();

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

                bool TryMoveEndpointByIndex(Entity writable, int endpointIndex, Point2d target, double moveTol)
                {
                    if (endpointIndex != 0 && endpointIndex != 1)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var old = endpointIndex == 0
                            ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                            : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        if (endpointIndex == 0)
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
                        var old = pl.GetPoint2dAt(endpointIndex);
                        if (old.GetDistanceTo(target) <= moveTol)
                        {
                            return false;
                        }

                        pl.SetPointAt(endpointIndex, target);
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

                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool Generated)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
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

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical, generatedSet.Contains(id)));
                }

                if (segments.Count < 2)
                {
                    tr.Commit();
                    return;
                }

                var adjusted = 0;
                const double tinyGap = 4.00;
                const double usecTwentyTwelveGap = 12.75;
                const double usecExtendedMinMajorGap = 2.00;
                const double moveTol = 0.01;
                var movedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string EndpointKey(int segIndex, int endpointIndex)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}:{1}",
                        segIndex,
                        endpointIndex);
                }

                double AxisOverlap(
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool Generated) a,
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, bool Generated) b)
                {
                    if (a.Horizontal && b.Horizontal)
                    {
                        var aMin = Math.Min(a.A.X, a.B.X);
                        var aMax = Math.Max(a.A.X, a.B.X);
                        var bMin = Math.Min(b.A.X, b.B.X);
                        var bMax = Math.Max(b.A.X, b.B.X);
                        return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                    }

                    if (a.Vertical && b.Vertical)
                    {
                        var aMin = Math.Min(a.A.Y, a.B.Y);
                        var aMax = Math.Max(a.A.Y, a.B.Y);
                        var bMin = Math.Min(b.A.Y, b.B.Y);
                        var bMax = Math.Max(b.A.Y, b.B.Y);
                        return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                    }

                    return 0.0;
                }

                bool HasCompanionAtOffset(
                    int segIndex,
                    double expectedOffset,
                    double tol,
                    double minOverlap,
                    bool requireSameLayer,
                    bool? requireCompanionGenerated = null)
                {
                    if (segIndex < 0 || segIndex >= segments.Count)
                    {
                        return false;
                    }

                    var s = segments[segIndex];
                    if (!s.Horizontal && !s.Vertical)
                    {
                        return false;
                    }

                    var sCoord = s.Horizontal
                        ? (0.5 * (s.A.Y + s.B.Y))
                        : (0.5 * (s.A.X + s.B.X));
                    for (var oi = 0; oi < segments.Count; oi++)
                    {
                        if (oi == segIndex)
                        {
                            continue;
                        }

                        var o = segments[oi];
                        if (s.Horizontal != o.Horizontal || s.Vertical != o.Vertical)
                        {
                            continue;
                        }

                        if (requireSameLayer &&
                            !string.Equals(s.Layer, o.Layer, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (requireCompanionGenerated.HasValue && o.Generated != requireCompanionGenerated.Value)
                        {
                            continue;
                        }

                        if (AxisOverlap(s, o) < minOverlap)
                        {
                            continue;
                        }

                        var oCoord = o.Horizontal
                            ? (0.5 * (o.A.Y + o.B.Y))
                            : (0.5 * (o.A.X + o.B.X));
                        var offset = Math.Abs(oCoord - sCoord);
                        if (Math.Abs(offset - expectedOffset) <= tol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var usecInnerTwentyTwelveStrict = new bool[segments.Count];
                var usecInnerTwentyTwelveLoose = new bool[segments.Count];
                var strictInnerCount = 0;
                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (!string.Equals(s.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Inner 20.12-in-30.18 indicator.
                    // Loose: previous geometry-only heuristic.
                    // Strict: requires a non-generated 20.12 companion so inner 20.12 is chosen
                    // over outer 30.18 when both are nearby.
                    var hasTenCompanion = HasCompanionAtOffset(i, 10.06, 1.75, 10.0, requireSameLayer: true);
                    var hasTwentyCompanion = HasCompanionAtOffset(i, 20.12, 2.50, 10.0, requireSameLayer: false);
                    var looseInner = hasTenCompanion && hasTwentyCompanion;
                    usecInnerTwentyTwelveLoose[i] = looseInner;

                    var strictInner = false;
                    if (looseInner && generatedSet.Count > 0)
                    {
                        var hasTwentyNonGeneratedCompanion = HasCompanionAtOffset(
                            i,
                            20.12,
                            2.50,
                            10.0,
                            requireSameLayer: false,
                            requireCompanionGenerated: false);
                        strictInner = hasTwentyNonGeneratedCompanion;
                    }

                    usecInnerTwentyTwelveStrict[i] = strictInner;
                    if (strictInner)
                    {
                        strictInnerCount++;
                    }
                }

                var usecInnerTwentyTwelve = strictInnerCount > 0
                    ? usecInnerTwentyTwelveStrict
                    : usecInnerTwentyTwelveLoose;

                for (var hi = 0; hi < segments.Count; hi++)
                {
                    var hSeg = segments[hi];
                    if (!hSeg.Horizontal)
                    {
                        continue;
                    }

                    for (var hEnd = 0; hEnd <= 1; hEnd++)
                    {
                        var hKey = EndpointKey(hi, hEnd);
                        if (movedEndpoints.Contains(hKey))
                        {
                            continue;
                        }

                        var hPoint = hEnd == 0 ? hSeg.A : hSeg.B;
                        var hInnerTwentyTwelve = usecInnerTwentyTwelve[hi];
                        var bestFound = false;
                        var bestScore = double.MaxValue;
                        var bestVi = -1;
                        var bestVEnd = -1;
                        var bestTarget = default(Point2d);
                        for (var vi = 0; vi < segments.Count; vi++)
                        {
                            if (vi == hi)
                            {
                                continue;
                            }

                            var vSeg = segments[vi];
                            if (!vSeg.Vertical)
                            {
                                continue;
                            }

                            for (var vEnd = 0; vEnd <= 1; vEnd++)
                            {
                                var vKey = EndpointKey(vi, vEnd);
                                if (movedEndpoints.Contains(vKey))
                                {
                                    continue;
                                }

                                var vPoint = vEnd == 0 ? vSeg.A : vSeg.B;
                                var vInnerTwentyTwelve = usecInnerTwentyTwelve[vi];
                                if (hInnerTwentyTwelve != vInnerTwentyTwelve &&
                                    (hInnerTwentyTwelve || vInnerTwentyTwelve))
                                {
                                    // Do not pair inner 20.12 candidates with outer corridor lines.
                                    continue;
                                }

                                if (!TryIntersectInfiniteLines(hSeg.A, hSeg.B, vSeg.A, vSeg.B, out var target))
                                {
                                    continue;
                                }

                                var hOther = hEnd == 0 ? hSeg.B : hSeg.A;
                                var hOut = hPoint - hOther;
                                if (hOut.Length <= 1e-6)
                                {
                                    continue;
                                }

                                var hAdvance = (target - hPoint).DotProduct(hOut / hOut.Length);
                                if (hAdvance < -moveTol)
                                {
                                    continue;
                                }

                                var vOther = vEnd == 0 ? vSeg.B : vSeg.A;
                                var vOut = vPoint - vOther;
                                if (vOut.Length <= 1e-6)
                                {
                                    continue;
                                }

                                var vAdvance = (target - vPoint).DotProduct(vOut / vOut.Length);
                                if (vAdvance < -moveTol)
                                {
                                    continue;
                                }

                                var dH = hPoint.GetDistanceTo(target);
                                var dV = vPoint.GetDistanceTo(target);
                                var allowExtendedUsecJoin = hInnerTwentyTwelve && vInnerTwentyTwelve;
                                var endpointGapLimit = allowExtendedUsecJoin ? usecTwentyTwelveGap : tinyGap;
                                if (dH > endpointGapLimit || dV > endpointGapLimit)
                                {
                                    continue;
                                }

                                var majorGap = Math.Max(dH, dV);
                                if (allowExtendedUsecJoin && majorGap <= usecExtendedMinMajorGap)
                                {
                                    // Prevent tiny snaps from stealing the endpoint needed for the 20.12 corner join.
                                    continue;
                                }

                                var sameLayer = string.Equals(hSeg.Layer, vSeg.Layer, StringComparison.OrdinalIgnoreCase);
                                var score =
                                    dH +
                                    dV -
                                    (sameLayer ? 0.05 : 0.0) -
                                    (allowExtendedUsecJoin ? 0.10 : 0.0) +
                                    (allowExtendedUsecJoin ? (0.15 * Math.Abs(majorGap - 10.06)) : 0.0);
                                if (!bestFound || score < (bestScore - 1e-9))
                                {
                                    bestFound = true;
                                    bestScore = score;
                                    bestVi = vi;
                                    bestVEnd = vEnd;
                                    bestTarget = target;
                                }
                            }
                        }

                        if (!bestFound || bestVi < 0 || bestVEnd < 0)
                        {
                            continue;
                        }

                        var hWritable = tr.GetObject(hSeg.Id, OpenMode.ForWrite, false) as Entity;
                        var vSegBest = segments[bestVi];
                        var vWritable = tr.GetObject(vSegBest.Id, OpenMode.ForWrite, false) as Entity;
                        if (hWritable == null || hWritable.IsErased || vWritable == null || vWritable.IsErased)
                        {
                            continue;
                        }

                        var movedH = TryMoveEndpointByIndex(hWritable, hEnd, bestTarget, moveTol);
                        var movedV = TryMoveEndpointByIndex(vWritable, bestVEnd, bestTarget, moveTol);
                        if (!movedH && !movedV)
                        {
                            continue;
                        }

                        adjusted++;
                        if (movedH)
                        {
                            movedEndpoints.Add(hKey);
                        }

                        if (movedV)
                        {
                            movedEndpoints.Add(EndpointKey(bestVi, bestVEnd));
                        }

                        if (TryReadOpenSegment(hWritable, out var newHA, out var newHB))
                        {
                            segments[hi] = (hSeg.Id, hSeg.Layer, newHA, newHB, IsHorizontalLike(newHA, newHB), IsVerticalLike(newHA, newHB), hSeg.Generated);
                        }

                        if (TryReadOpenSegment(vWritable, out var newVA, out var newVB))
                        {
                            segments[bestVi] = (vSegBest.Id, vSegBest.Layer, newVA, newVB, IsHorizontalLike(newVA, newVB), IsVerticalLike(newVA, newVB), vSegBest.Generated);
                        }
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: corner-gap-closed {adjusted} orthogonal endpoint gap(s) on L-SEC/L-USEC.");
                }
            }
        }

        private static void NormalizeShortRoadAllowanceLayersByNeighborhood(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

                var generatedSet = generatedRoadAllowanceIds == null
                    ? new HashSet<ObjectId>()
                    : new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
                var generatedSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
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

                    var len = a.GetDistanceTo(b);
                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer, a, b, horizontal, vertical, len));
                    if (generatedSet.Contains(id))
                    {
                        generatedSegments.Add((id, a, b, horizontal, vertical, len));
                    }
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double candidateMinLen = 2.0;
                const double candidateMaxLen = 120.0;
                const double neighborAxisTol = 1.50;
                const double neighborEndTol = 2.50;
                const double neighborMinLen = 6.0;
                const double referenceDistanceTol = 1.20;
                const double corridorAxisTol = 31.0;
                const double corridorOverlapMin = 20.0;

                bool IsNearGeneratedCorridor((ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length) candidate)
                {
                    if (generatedSegments.Count == 0)
                    {
                        return false;
                    }

                    for (var gi = 0; gi < generatedSegments.Count; gi++)
                    {
                        var g = generatedSegments[gi];
                        if ((candidate.Horizontal != g.Horizontal) || (candidate.Vertical != g.Vertical))
                        {
                            continue;
                        }

                        if (candidate.Horizontal)
                        {
                            var yCandidate = 0.5 * (candidate.A.Y + candidate.B.Y);
                            var yGenerated = 0.5 * (g.A.Y + g.B.Y);
                            if (Math.Abs(yCandidate - yGenerated) > corridorAxisTol)
                            {
                                continue;
                            }

                            var cMin = Math.Min(candidate.A.X, candidate.B.X);
                            var cMax = Math.Max(candidate.A.X, candidate.B.X);
                            var gMin = Math.Min(g.A.X, g.B.X);
                            var gMax = Math.Max(g.A.X, g.B.X);
                            var overlap = Math.Min(cMax, gMax) - Math.Max(cMin, gMin);
                            if (overlap < Math.Max(corridorOverlapMin, Math.Min(candidate.Length, g.Length) * 0.35))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            var xCandidate = 0.5 * (candidate.A.X + candidate.B.X);
                            var xGenerated = 0.5 * (g.A.X + g.B.X);
                            if (Math.Abs(xCandidate - xGenerated) > corridorAxisTol)
                            {
                                continue;
                            }

                            var cMin = Math.Min(candidate.A.Y, candidate.B.Y);
                            var cMax = Math.Max(candidate.A.Y, candidate.B.Y);
                            var gMin = Math.Min(g.A.Y, g.B.Y);
                            var gMax = Math.Max(g.A.Y, g.B.Y);
                            var overlap = Math.Min(cMax, gMax) - Math.Max(cMin, gMin);
                            if (overlap < Math.Max(corridorOverlapMin, Math.Min(candidate.Length, g.Length) * 0.35))
                            {
                                continue;
                            }
                        }

                        return true;
                    }

                    return false;
                }

                var inspected = 0;
                var normalized = 0;
                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (s.Length < candidateMinLen || s.Length > candidateMaxLen)
                    {
                        continue;
                    }

                    if (!s.Horizontal && !s.Vertical)
                    {
                        continue;
                    }

                    // Keep 30.18/20.12 generated RA corridors stable; they are normalized separately.
                    if (IsNearGeneratedCorridor(s))
                    {
                        continue;
                    }

                    inspected++;
                    var yS = 0.5 * (s.A.Y + s.B.Y);
                    var xS = 0.5 * (s.A.X + s.B.X);

                    double secVotesA = 0.0;
                    double usecVotesA = 0.0;
                    double secVotesB = 0.0;
                    double usecVotesB = 0.0;
                    var secCountA = 0;
                    var usecCountA = 0;
                    var secCountB = 0;
                    var usecCountB = 0;
                    var bestSecScore = double.MaxValue;
                    var bestUsecScore = double.MaxValue;

                    for (var j = 0; j < segments.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var o = segments[j];
                        if (o.Length < neighborMinLen)
                        {
                            continue;
                        }

                        if (s.Horizontal != o.Horizontal || s.Vertical != o.Vertical)
                        {
                            continue;
                        }

                        if (s.Horizontal)
                        {
                            var yO = 0.5 * (o.A.Y + o.B.Y);
                            if (Math.Abs(yS - yO) > neighborAxisTol)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            var xO = 0.5 * (o.A.X + o.B.X);
                            if (Math.Abs(xS - xO) > neighborAxisTol)
                            {
                                continue;
                            }
                        }

                        var dA = Math.Min(s.A.GetDistanceTo(o.A), s.A.GetDistanceTo(o.B));
                        var dB = Math.Min(s.B.GetDistanceTo(o.A), s.B.GetDistanceTo(o.B));
                        var isSec = string.Equals(o.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                        var isUsec = string.Equals(o.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        if (!isSec && !isUsec)
                        {
                            continue;
                        }

                        var daRef = DistancePointToSegment(s.A, o.A, o.B);
                        var dbRef = DistancePointToSegment(s.B, o.A, o.B);
                        if (daRef <= referenceDistanceTol || dbRef <= referenceDistanceTol)
                        {
                            var score = daRef + dbRef;
                            if (isSec)
                            {
                                if (score < bestSecScore)
                                {
                                    bestSecScore = score;
                                }
                            }
                            else if (score < bestUsecScore)
                            {
                                bestUsecScore = score;
                            }
                        }

                        if (dA <= neighborEndTol)
                        {
                            var wA = 1.0 + (neighborEndTol - dA);
                            if (isSec)
                            {
                                secVotesA += wA;
                                secCountA++;
                            }
                            else
                            {
                                usecVotesA += wA;
                                usecCountA++;
                            }
                        }

                        if (dB <= neighborEndTol)
                        {
                            var wB = 1.0 + (neighborEndTol - dB);
                            if (isSec)
                            {
                                secVotesB += wB;
                                secCountB++;
                            }
                            else
                            {
                                usecVotesB += wB;
                                usecCountB++;
                            }
                        }
                    }

                    string? bestEndA = null;
                    if (secVotesA > usecVotesA + 0.25)
                    {
                        bestEndA = "L-SEC";
                    }
                    else if (usecVotesA > secVotesA + 0.25)
                    {
                        bestEndA = "L-USEC";
                    }

                    string? bestEndB = null;
                    if (secVotesB > usecVotesB + 0.25)
                    {
                        bestEndB = "L-SEC";
                    }
                    else if (usecVotesB > secVotesB + 0.25)
                    {
                        bestEndB = "L-USEC";
                    }

                    string? targetLayer = null;
                    if (!string.IsNullOrWhiteSpace(bestEndA) &&
                        !string.IsNullOrWhiteSpace(bestEndB) &&
                        string.Equals(bestEndA, bestEndB, StringComparison.OrdinalIgnoreCase))
                    {
                        targetLayer = bestEndA;
                    }
                    else
                    {
                        var secVotes = secVotesA + secVotesB;
                        var usecVotes = usecVotesA + usecVotesB;
                        var secCount = secCountA + secCountB;
                        var usecCount = usecCountA + usecCountB;
                        if (secCount >= 2 || usecCount >= 2)
                        {
                            if (secVotes > usecVotes + 1.00)
                            {
                                targetLayer = "L-SEC";
                            }
                            else if (usecVotes > secVotes + 1.00)
                            {
                                targetLayer = "L-USEC";
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(targetLayer) && s.Length <= 80.0)
                    {
                        if (bestSecScore < double.MaxValue || bestUsecScore < double.MaxValue)
                        {
                            if (bestSecScore == double.MaxValue)
                            {
                                targetLayer = "L-USEC";
                            }
                            else if (bestUsecScore == double.MaxValue)
                            {
                                targetLayer = "L-SEC";
                            }
                            else
                            {
                                var diff = Math.Abs(bestSecScore - bestUsecScore);
                                if (diff >= 0.35)
                                {
                                    targetLayer = bestSecScore < bestUsecScore
                                        ? "L-SEC"
                                        : "L-USEC";
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(targetLayer) ||
                        string.Equals(s.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(s.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased)
                    {
                        continue;
                    }

                    writable.Layer = targetLayer;
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: neighborhood-normalized {normalized} boundary segment(s) layer (inspected {inspected}).");
            }
        }

        private static void NormalizeHorizontalSecRoadAllowanceLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

                    // Accept multi-vertex open polylines only when they are effectively collinear.
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

            bool HorizontalOverlaps((Point2d A, Point2d B) a, (Point2d A, Point2d B) b, double minOverlap)
            {
                var aMin = Math.Min(a.A.X, a.B.X);
                var aMax = Math.Max(a.A.X, a.B.X);
                var bMin = Math.Min(b.A.X, b.B.X);
                var bMax = Math.Max(b.A.X, b.B.X);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedHorizontals = new List<(ObjectId Id, Point2d A, Point2d B, double Y, double Length)>();
                var secCandidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Y, double Length)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b) || !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 8.0)
                    {
                        continue;
                    }

                    var y = 0.5 * (a.Y + b.Y);
                    if (generatedSet.Contains(id))
                    {
                        generatedHorizontals.Add((id, a, b, y, len));
                    }
                    else
                    {
                        secCandidates.Add((id, ent.Layer ?? string.Empty, a, b, y, len));
                    }
                }

                if (generatedHorizontals.Count == 0 || secCandidates.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double usecBandTol = 34.5;
                const double secTargetMin = 15.5;
                const double secTargetMax = 24.5;
                const double usecCompanionMin = 7.0;
                const double usecCompanionMax = 13.8;
                const double candidateMinLen = 10.0;
                const double candidateMaxLen = 140.0;
                const double neighborAxisTol = 0.80;
                const double neighborOverlapMin = 8.0;

                (bool SecLeft, bool SecRight, bool UsecLeft, bool UsecRight) AnalyzeNeighbors(int index)
                {
                    var s = secCandidates[index];
                    var sCenterX = 0.5 * (s.A.X + s.B.X);
                    var secLeft = false;
                    var secRight = false;
                    var usecLeft = false;
                    var usecRight = false;
                    for (var j = 0; j < secCandidates.Count; j++)
                    {
                        if (index == j)
                        {
                            continue;
                        }

                        var o = secCandidates[j];
                        if (Math.Abs(s.Y - o.Y) > neighborAxisTol)
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((s.A, s.B), (o.A, o.B), minOverlap: neighborOverlapMin))
                        {
                            continue;
                        }

                        var oCenterX = 0.5 * (o.A.X + o.B.X);
                        var left = oCenterX < (sCenterX - 0.25);
                        var right = oCenterX > (sCenterX + 0.25);
                        if (!left && !right)
                        {
                            left = true;
                            right = true;
                        }

                        if (string.Equals(o.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                        {
                            if (left)
                            {
                                secLeft = true;
                            }

                            if (right)
                            {
                                secRight = true;
                            }
                        }
                        else if (string.Equals(o.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                        {
                            if (left)
                            {
                                usecLeft = true;
                            }

                            if (right)
                            {
                                usecRight = true;
                            }
                        }
                    }

                    return (secLeft, secRight, usecLeft, usecRight);
                }

                bool HasThirtyEighteenCompanion(int generatedIndex, int candidateIndex)
                {
                    var g = generatedHorizontals[generatedIndex];
                    var s = secCandidates[candidateIndex];
                    for (var j = 0; j < secCandidates.Count; j++)
                    {
                        if (j == candidateIndex)
                        {
                            continue;
                        }

                        var o = secCandidates[j];
                        if (o.Length < candidateMinLen || o.Length > candidateMaxLen)
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((o.A, o.B), (g.A, g.B), minOverlap: 20.0))
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((o.A, o.B), (s.A, s.B), minOverlap: neighborOverlapMin))
                        {
                            continue;
                        }

                        var dy = Math.Abs(o.Y - g.Y);
                        if (dy >= usecCompanionMin && dy <= usecCompanionMax)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var normalized = 0;
                var skippedThirtyEighteen = 0;
                for (var i = 0; i < secCandidates.Count; i++)
                {
                    var s = secCandidates[i];
                    if (!string.Equals(s.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (s.Length < candidateMinLen || s.Length > candidateMaxLen)
                    {
                        continue;
                    }

                    var bestDy = double.MaxValue;
                    var bestGeneratedIndex = -1;
                    for (var gi = 0; gi < generatedHorizontals.Count; gi++)
                    {
                        var g = generatedHorizontals[gi];
                        var dy = Math.Abs(s.Y - g.Y);
                        if (dy < 1.0 || dy > usecBandTol)
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((s.A, s.B), (g.A, g.B), minOverlap: 20.0))
                        {
                            continue;
                        }

                        if (dy < bestDy)
                        {
                            bestDy = dy;
                            bestGeneratedIndex = gi;
                        }
                    }

                    if (bestDy == double.MaxValue)
                    {
                        continue;
                    }

                    if (bestDy < secTargetMin || bestDy > secTargetMax)
                    {
                        continue;
                    }

                    // Guard: when a 10.06 companion exists for the same generated corridor, this is a 30.18
                    // RA boundary and should remain L-USEC (do not misclassify as 20.12 L-SEC).
                    if (bestGeneratedIndex >= 0 && HasThirtyEighteenCompanion(bestGeneratedIndex, i))
                    {
                        skippedThirtyEighteen++;
                        continue;
                    }

                    var neighbors = AnalyzeNeighbors(i);
                    var hasSecSupport =
                        (neighbors.SecLeft && neighbors.SecRight) ||
                        ((neighbors.SecLeft || neighbors.SecRight) && !(neighbors.UsecLeft || neighbors.UsecRight));
                    if (!hasSecSupport)
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(s.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = "L-SEC";
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                if (normalized > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized {normalized} horizontal 20.12 road allowance segment(s) to L-SEC.");
                }
                if (skippedThirtyEighteen > 0)
                {
                    logger?.WriteLine($"Cleanup: skipped {skippedThirtyEighteen} horizontal candidate(s) that matched 30.18 corridor companion pattern.");
                }
            }
        }

        private static void NormalizeBottomTownshipBoundaryLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool HorizontalOverlaps((Point2d A, Point2d B) a, (Point2d A, Point2d B) b, double minOverlap)
            {
                var aMin = Math.Min(a.A.X, a.B.X);
                var aMax = Math.Max(a.A.X, a.B.X);
                var bMin = Math.Min(b.A.X, b.B.X);
                var bMax = Math.Max(b.A.X, b.B.X);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedBottomY = double.MaxValue;
                var generatedHorizontals = new List<(Point2d A, Point2d B, double Y)>();
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Y, double Length)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b) ||
                        !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 4.0)
                    {
                        continue;
                    }

                    var y = 0.5 * (a.Y + b.Y);
                    if (generatedSet.Contains(id))
                    {
                        generatedHorizontals.Add((a, b, y));
                        if (y < generatedBottomY)
                        {
                            generatedBottomY = y;
                        }
                    }

                    candidates.Add((id, ent.Layer ?? string.Empty, a, b, y, len));
                }

                if (candidates.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var candidateBottomY = candidates.Min(c => c.Y);
                var seamBaseY = generatedBottomY == double.MaxValue
                    ? candidateBottomY
                    : Math.Min(candidateBottomY, generatedBottomY);

                // Only fix around the bottom township seam where layering drifts.
                const double seamBandBelow = 20.0;
                const double seamBandAbove = 80.0;
                const double secTargetMin = 15.5;
                const double secTargetMax = 24.5;
                const double usecTargetMin = 25.5;
                const double usecTargetMax = 34.5;
                const double dyOverlapMin = 5.0;
                const double expectedSecDy = 20.12;
                const double expectedUsecDy = 30.18;
                const double expectedDyTol = 6.0;
                const double expectedDyDecisionMargin = 0.45;

                double ComputeBestExpectedScore(int index, double expectedDy)
                {
                    var s = candidates[index];
                    var bestScore = double.MaxValue;
                    for (var oi = 0; oi < candidates.Count; oi++)
                    {
                        if (oi == index)
                        {
                            continue;
                        }

                        var o = candidates[oi];
                        if (o.Y < (seamBaseY - seamBandBelow) ||
                            o.Y > (seamBaseY + seamBandAbove))
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((s.A, s.B), (o.A, o.B), minOverlap: dyOverlapMin))
                        {
                            continue;
                        }

                        var dy = Math.Abs(s.Y - o.Y);
                        if (dy < 1.0)
                        {
                            continue;
                        }

                        var score = Math.Abs(dy - expectedDy);
                        if (score < bestScore)
                        {
                            bestScore = score;
                        }
                    }

                    return bestScore;
                }

                var normalized = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (c.Y < (seamBaseY - seamBandBelow) ||
                        c.Y > (seamBaseY + seamBandAbove))
                    {
                        continue;
                    }

                    var canonicalSecScore = ComputeBestExpectedScore(i, expectedSecDy);
                    var canonicalUsecScore = ComputeBestExpectedScore(i, expectedUsecDy);
                    var canonicalDecision = false;
                    var bestDy = double.MaxValue;
                    string? targetLayer = null;
                    if (canonicalSecScore <= expectedDyTol &&
                        (canonicalSecScore + expectedDyDecisionMargin) < canonicalUsecScore)
                    {
                        targetLayer = "L-SEC";
                        canonicalDecision = true;
                    }
                    else if (canonicalUsecScore <= expectedDyTol &&
                             (canonicalUsecScore + expectedDyDecisionMargin) < canonicalSecScore)
                    {
                        targetLayer = "L-USEC";
                        canonicalDecision = true;
                    }

                    if (!canonicalDecision)
                    {
                        for (var gi = 0; gi < generatedHorizontals.Count; gi++)
                        {
                            var g = generatedHorizontals[gi];
                            if (!HorizontalOverlaps((c.A, c.B), (g.A, g.B), minOverlap: dyOverlapMin))
                            {
                                continue;
                            }

                            var dy = Math.Abs(c.Y - g.Y);
                            if (dy < 1.0)
                            {
                                continue;
                            }

                            if (dy < bestDy)
                            {
                                bestDy = dy;
                            }
                        }

                        if (bestDy == double.MaxValue)
                        {
                            for (var oi = 0; oi < candidates.Count; oi++)
                            {
                                if (oi == i)
                                {
                                    continue;
                                }

                                var o = candidates[oi];
                                if (o.Y < (seamBaseY - seamBandBelow) ||
                                    o.Y > (seamBaseY + seamBandAbove))
                                {
                                    continue;
                                }

                                if (!HorizontalOverlaps((c.A, c.B), (o.A, o.B), minOverlap: dyOverlapMin))
                                {
                                    continue;
                                }

                                var dy = Math.Abs(c.Y - o.Y);
                                if (dy < 1.0)
                                {
                                    continue;
                                }

                                if (dy < bestDy)
                                {
                                    bestDy = dy;
                                }
                            }
                        }

                        if (bestDy < usecTargetMin || bestDy > usecTargetMax)
                        {
                            if (bestDy < secTargetMin || bestDy > secTargetMax)
                            {
                                continue;
                            }
                        }

                        targetLayer = bestDy >= usecTargetMin && bestDy <= usecTargetMax
                            ? "L-USEC"
                            : "L-SEC";
                    }

                    if (targetLayer == null)
                    {
                        continue;
                    }

                    if (string.Equals(c.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(c.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = targetLayer;
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                if (normalized > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized {normalized} bottom-township seam segment(s) to expected L-SEC/L-USEC layer.");
                }
            }
        }

        private static void NormalizeRangeEdgeHorizontalRoadAllowanceLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            bool HorizontalOverlaps((Point2d A, Point2d B) a, (Point2d A, Point2d B) b, double minOverlap)
            {
                var aMin = Math.Min(a.A.X, a.B.X);
                var aMax = Math.Max(a.A.X, a.B.X);
                var bMin = Math.Min(b.A.X, b.B.X);
                var bMax = Math.Max(b.A.X, b.B.X);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);
            const double edgeBand = 145.0;
            const double minSegmentLength = 4.0;
            const double overlapMin = 8.0;
            const double usecNearMin = 7.0;    // 10.06 +- tolerance
            const double usecNearMax = 13.8;
            const double usecFarMin = 16.6;    // 20.12 +- tolerance
            const double usecFarMax = 23.8;

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Y, bool Generated)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b) ||
                        !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    if (a.GetDistanceTo(b) < minSegmentLength)
                    {
                        continue;
                    }

                    var y = 0.5 * (a.Y + b.Y);
                    var generated = generatedSet.Contains(id);
                    candidates.Add((id, ent.Layer ?? string.Empty, a, b, y, generated));
                }

                if (candidates.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                bool IsRangeEdgeCandidate((ObjectId Id, string Layer, Point2d A, Point2d B, double Y, bool Generated) c)
                {
                    var segMinX = Math.Min(c.A.X, c.B.X);
                    var segMaxX = Math.Max(c.A.X, c.B.X);
                    var touchesWestEdge = segMinX <= (clipMinX + edgeBand);
                    var touchesEastEdge = segMaxX >= (clipMaxX - edgeBand);
                    return touchesWestEdge || touchesEastEdge;
                }

                var targets = new Dictionary<ObjectId, string>();
                var inspectedGenerated = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var g = candidates[i];
                    if (!g.Generated || !IsRangeEdgeCandidate(g))
                    {
                        continue;
                    }

                    inspectedGenerated++;

                    // Generated RA offsets represent the 20.12 interior corridor line.
                    if (!string.Equals(g.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        targets[g.Id] = "L-SEC";
                    }

                    for (var j = 0; j < candidates.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var c = candidates[j];
                        if (c.Generated || !IsRangeEdgeCandidate(c))
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((g.A, g.B), (c.A, c.B), minOverlap: overlapMin))
                        {
                            continue;
                        }

                        var dy = Math.Abs(c.Y - g.Y);
                        var shouldUsec =
                            (dy >= usecNearMin && dy <= usecNearMax) ||
                            (dy >= usecFarMin && dy <= usecFarMax);
                        if (!shouldUsec)
                        {
                            continue;
                        }

                        if (!string.Equals(c.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                        {
                            targets[c.Id] = "L-USEC";
                        }
                    }
                }

                if (targets.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var normalizedGenerated = 0;
                var normalizedBoundary = 0;
                foreach (var kvp in targets)
                {
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(kvp.Key, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, kvp.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = kvp.Value;
                    writable.ColorIndex = 256;
                    if (string.Equals(kvp.Value, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedGenerated++;
                    }
                    else
                    {
                        normalizedBoundary++;
                    }
                }

                tr.Commit();
                if (normalizedGenerated > 0 || normalizedBoundary > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: normalized {normalizedGenerated} range-edge generated 20.12 segment(s) to L-SEC and {normalizedBoundary} adjoining range-edge segment(s) to L-USEC (inspected generated={inspectedGenerated}).");
                }
            }
        }

        private static void NormalizeThirtyEighteenCorridorLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var segments = new List<(
                    ObjectId Id,
                    string Layer,
                    Point2d A,
                    Point2d B,
                    bool Horizontal,
                    bool Vertical,
                    double Length,
                    double Coord,
                    double SpanMin,
                    double SpanMax)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 25.0)
                    {
                        continue;
                    }

                    var coord = horizontal
                        ? (0.5 * (a.Y + b.Y))
                        : (0.5 * (a.X + b.X));
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    segments.Add((id, ent.Layer ?? string.Empty, a, b, horizontal, vertical, len, coord, spanMin, spanMax));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                double OverlapAmount(
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length, double Coord, double SpanMin, double SpanMax) s1,
                    (ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length, double Coord, double SpanMin, double SpanMax) s2)
                {
                    return Math.Min(s1.SpanMax, s2.SpanMax) - Math.Max(s1.SpanMin, s2.SpanMin);
                }

                const double d10Min = 8.6;
                const double d10Max = 11.8;
                const double d20Min = 18.6;
                const double d20Max = 21.8;
                const double d30Min = 28.8;
                const double d30Max = 31.8;
                const double overlapMin = 18.0;
                static bool InRange(double value, double min, double max) => value >= min && value <= max;

                var usecVotes = new Dictionary<ObjectId, int>();
                void VoteUsec(ObjectId id)
                {
                    if (usecVotes.TryGetValue(id, out var n))
                    {
                        usecVotes[id] = n + 1;
                    }
                    else
                    {
                        usecVotes[id] = 1;
                    }
                }

                int RunOrientation(bool horizontal)
                {
                    var indexes = new List<int>();
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var s = segments[i];
                        if (horizontal ? s.Horizontal : s.Vertical)
                        {
                            indexes.Add(i);
                        }
                    }

                    var patternMatches = 0;
                    for (var mi = 0; mi < indexes.Count; mi++)
                    {
                        var mIdx = indexes[mi];
                        var m = segments[mIdx];
                        var near10 = new List<int>();
                        var near20 = new List<int>();
                        for (var oi = 0; oi < indexes.Count; oi++)
                        {
                            if (oi == mi)
                            {
                                continue;
                            }

                            var oIdx = indexes[oi];
                            var o = segments[oIdx];
                            if (OverlapAmount(m, o) < overlapMin)
                            {
                                continue;
                            }

                            var d = Math.Abs(m.Coord - o.Coord);
                            if (InRange(d, d10Min, d10Max))
                            {
                                near10.Add(oIdx);
                            }
                            else if (InRange(d, d20Min, d20Max))
                            {
                                near20.Add(oIdx);
                            }
                        }

                        if (near10.Count == 0 || near20.Count == 0)
                        {
                            continue;
                        }

                        var matchedThisMid = false;
                        for (var i10 = 0; i10 < near10.Count; i10++)
                        {
                            var a = segments[near10[i10]];
                            var sideA = Math.Sign(a.Coord - m.Coord);
                            if (sideA == 0)
                            {
                                continue;
                            }

                            for (var i20 = 0; i20 < near20.Count; i20++)
                            {
                                var b = segments[near20[i20]];
                                var sideB = Math.Sign(b.Coord - m.Coord);
                                if (sideB == 0 || sideA == sideB)
                                {
                                    continue;
                                }

                                if (OverlapAmount(a, b) < overlapMin)
                                {
                                    continue;
                                }

                                var dAB = Math.Abs(a.Coord - b.Coord);
                                if (!InRange(dAB, d30Min, d30Max))
                                {
                                    continue;
                                }

                                // 30.18 corridor pattern:
                                // middle 20.12 plus 10.06/20.11 companions are all L-USEC.
                                VoteUsec(m.Id);
                                VoteUsec(a.Id);
                                VoteUsec(b.Id);
                                matchedThisMid = true;
                            }
                        }

                        if (matchedThisMid)
                        {
                            patternMatches++;
                        }
                    }

                    return patternMatches;
                }

                var matchesH = RunOrientation(horizontal: true);
                var matchesV = RunOrientation(horizontal: false);

                if (usecVotes.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var normalizedToSec = 0;
                var normalizedToUsec = 0;
                foreach (var id in usecVotes.Keys)
                {
                    const string target = "L-USEC";

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, target, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = target;
                    writable.ColorIndex = 256;
                    if (string.Equals(target, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedToSec++;
                    }
                    else
                    {
                        normalizedToUsec++;
                    }
                }

                tr.Commit();
                if (normalizedToSec > 0 || normalizedToUsec > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: corridor-normalized {normalizedToSec} segment(s) to L-SEC and {normalizedToUsec} segment(s) to L-USEC (30.18 pattern matches H={matchesH}, V={matchesV}).");
                }
            }
        }

        private static void StitchTrimmedContextSectionEndpoints(
            Database database,
            IReadOnlyCollection<ObjectId> contextSectionIds,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || contextSectionIds == null || contextSectionIds.Count == 0 || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
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
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

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

                Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
                {
                    var ab = b - a;
                    var len2 = ab.DotProduct(ab);
                    if (len2 <= 1e-12)
                    {
                        return a;
                    }

                    var t = (p - a).DotProduct(ab) / len2;
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    return a + (ab * t);
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

                var contextSet = new HashSet<ObjectId>(contextSectionIds.Where(id => !id.IsNull));
                var allBoundarySegments = new List<(ObjectId Id, bool IsContext, Point2d A, Point2d B)>();
                var endpointAnchors = new List<(ObjectId Id, bool IsContext, Point2d P)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
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

                    var isContext = contextSet.Contains(id);
                    allBoundarySegments.Add((id, isContext, a, b));
                    endpointAnchors.Add((id, isContext, a));
                    endpointAnchors.Add((id, isContext, b));
                }

                if (allBoundarySegments.Count == 0 || endpointAnchors.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointSnapTol = 1.15;
                const double segmentSnapTol = 0.95;
                const double moveTol = 0.02;
                var adjusted = 0;
                foreach (var id in contextSet.ToList())
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var p0, out var p1))
                    {
                        continue;
                    }

                    bool TryBestEndpointSnap(Point2d endpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDist = double.MaxValue;
                        var bestTargetIsContext = true;
                        for (var i = 0; i < endpointAnchors.Count; i++)
                        {
                            var anchor = endpointAnchors[i];
                            if (anchor.Id == id)
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(anchor.P);
                            if (d > endpointSnapTol)
                            {
                                continue;
                            }

                            var prefer = !anchor.IsContext;
                            var better = !found;
                            if (!better)
                            {
                                if (prefer && bestTargetIsContext)
                                {
                                    better = true;
                                }
                                else if (prefer == !bestTargetIsContext && d < (bestDist - 1e-9))
                                {
                                    better = true;
                                }
                            }

                            if (!better)
                            {
                                continue;
                            }

                            found = true;
                            bestDist = d;
                            bestTargetIsContext = anchor.IsContext;
                            snapped = anchor.P;
                        }

                        return found;
                    }

                    bool TryBestSegmentSnap(Point2d endpoint, Point2d otherEndpoint, out Point2d snapped)
                    {
                        snapped = endpoint;
                        var found = false;
                        var bestDist = double.MaxValue;
                        var thisIsHorizontal = IsHorizontalLike(endpoint, otherEndpoint);
                        var thisIsVertical = IsVerticalLike(endpoint, otherEndpoint);
                        for (var i = 0; i < allBoundarySegments.Count; i++)
                        {
                            var seg = allBoundarySegments[i];
                            if (seg.Id == id)
                            {
                                continue;
                            }

                            var segIsHorizontal = IsHorizontalLike(seg.A, seg.B);
                            var segIsVertical = IsVerticalLike(seg.A, seg.B);
                            if ((thisIsHorizontal && !segIsHorizontal) ||
                                (thisIsVertical && !segIsVertical))
                            {
                                continue;
                            }

                            var candidate = ClosestPointOnSegment(endpoint, seg.A, seg.B);
                            var d = endpoint.GetDistanceTo(candidate);
                            if (d > segmentSnapTol)
                            {
                                continue;
                            }

                            if (!found || d < (bestDist - 1e-9))
                            {
                                found = true;
                                bestDist = d;
                                snapped = candidate;
                            }
                        }

                        return found;
                    }

                    var new0 = p0;
                    var new1 = p1;
                    if (!TryBestEndpointSnap(new0, out new0))
                    {
                        TryBestSegmentSnap(p0, p1, out new0);
                    }

                    if (!TryBestEndpointSnap(new1, out new1))
                    {
                        TryBestSegmentSnap(p1, p0, out new1);
                    }

                    if (new0.GetDistanceTo(p0) <= moveTol && new1.GetDistanceTo(p1) <= moveTol)
                    {
                        continue;
                    }

                    if (TryWriteOpenSegment(ent, new0, new1))
                    {
                        adjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: stitched {adjusted} trimmed context segment endpoint(s) to nearby final boundaries.");
                }
            }
        }

        private static void RebuildLsdLabelsAtFinalIntersections(
            Database database,
            IEnumerable<QuarterLabelInfo> quarterInfos,
            Logger? logger)
        {
            if (database == null || quarterInfos == null)
            {
                return;
            }

            var uniqueQuarterInfos = new Dictionary<ObjectId, QuarterLabelInfo>();
            foreach (var info in quarterInfos)
            {
                if (info == null || info.QuarterId.IsNull || info.QuarterId.IsErased)
                {
                    continue;
                }

                if (!uniqueQuarterInfos.ContainsKey(info.QuarterId))
                {
                    uniqueQuarterInfos.Add(info.QuarterId, info);
                }
            }

            if (uniqueQuarterInfos.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, uniqueQuarterInfos.Keys, 100.0);
            bool IsPointInAnyWindow(Point2d p)
            {
                if (clipWindows == null || clipWindows.Count == 0)
                {
                    return true;
                }

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

                bool TryGetEntityCenter(Entity ent, out Point2d center)
                {
                    center = default;
                    if (ent == null)
                    {
                        return false;
                    }

                    try
                    {
                        var ext = ent.GeometricExtents;
                        center = new Point2d(
                            0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                            0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                        return true;
                    }
                    catch
                    {
                        if (ent is DBText dbt)
                        {
                            center = new Point2d(dbt.Position.X, dbt.Position.Y);
                            return true;
                        }

                        if (ent is MText mt)
                        {
                            center = new Point2d(mt.Location.X, mt.Location.Y);
                            return true;
                        }

                        if (ent is BlockReference br)
                        {
                            center = new Point2d(br.Position.X, br.Position.Y);
                            return true;
                        }
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

                bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection)
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
                    var u = Cross2d(diff, da) / denom;
                    if (t < -1e-6 || t > 1.0 + 1e-6 || u < -1e-6 || u > 1.0 + 1e-6)
                    {
                        return false;
                    }

                    intersection = a0 + (da * t);
                    return true;
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

                bool IsPointInExpandedExtents(Point2d p, Extents3d ext, double tol)
                {
                    return p.X >= (ext.MinPoint.X - tol) && p.X <= (ext.MaxPoint.X + tol) &&
                           p.Y >= (ext.MinPoint.Y - tol) && p.Y <= (ext.MaxPoint.Y + tol);
                }

                EnsureLayer(database, tr, "L-SECTION-LSD");
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var horizontalLsdSegments = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var verticalLsdSegments = new List<(Point2d A, Point2d B, Point2d Mid)>();
                var oldLabelEntities = new Dictionary<ObjectId, Point2d>();
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryReadOpenSegment(ent, out var a, out var b))
                    {
                        if (!DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        var mid = Midpoint(a, b);
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            if (IsHorizontalLike(a, b))
                            {
                                horizontalLsdSegments.Add((a, b, mid));
                            }
                            else if (IsVerticalLike(a, b))
                            {
                                verticalLsdSegments.Add((a, b, mid));
                            }
                        }
                        else if (IsPointInAnyWindow(mid))
                        {
                            oldLabelEntities[id] = mid;
                        }

                        continue;
                    }

                    if (TryGetEntityCenter(ent, out var center) && IsPointInAnyWindow(center))
                    {
                        oldLabelEntities[id] = center;
                    }
                }

                if (horizontalLsdSegments.Count == 0 || verticalLsdSegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var placed = 0;
                var erased = 0;
                var skipped = 0;
                var consumedOldLabelIds = new HashSet<ObjectId>();
                foreach (var pair in uniqueQuarterInfos)
                {
                    var info = pair.Value;
                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline quarter) || quarter.IsErased)
                    {
                        continue;
                    }

                    var blockName = GetLsdLabelBlockName(info.Quarter);
                    if (string.IsNullOrWhiteSpace(blockName))
                    {
                        continue;
                    }

                    Extents3d quarterExtents;
                    try
                    {
                        quarterExtents = quarter.GeometricExtents;
                    }
                    catch
                    {
                        skipped++;
                        continue;
                    }

                    var quarterCenter = new Point2d(
                        0.5 * (quarterExtents.MinPoint.X + quarterExtents.MaxPoint.X),
                        0.5 * (quarterExtents.MinPoint.Y + quarterExtents.MaxPoint.Y));
                    const double quarterTol = 2.0;
                    var horizontalCandidates = horizontalLsdSegments
                        .Where(s => IsPointInExpandedExtents(s.Mid, quarterExtents, quarterTol))
                        .ToList();
                    var verticalCandidates = verticalLsdSegments
                        .Where(s => IsPointInExpandedExtents(s.Mid, quarterExtents, quarterTol))
                        .ToList();
                    if (horizontalCandidates.Count == 0 || verticalCandidates.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    var foundTarget = false;
                    var target = default(Point2d);
                    var bestScore = double.MaxValue;
                    for (var hi = 0; hi < horizontalCandidates.Count; hi++)
                    {
                        var h = horizontalCandidates[hi];
                        for (var vi = 0; vi < verticalCandidates.Count; vi++)
                        {
                            var v = verticalCandidates[vi];
                            if (!TryIntersectSegments(h.A, h.B, v.A, v.B, out var intersection))
                            {
                                continue;
                            }

                            if (!IsPointInExpandedExtents(intersection, quarterExtents, quarterTol))
                            {
                                continue;
                            }

                            var score = quarterCenter.GetDistanceTo(intersection);
                            if (!foundTarget || score < bestScore)
                            {
                                foundTarget = true;
                                bestScore = score;
                                target = intersection;
                            }
                        }
                    }

                    if (!foundTarget)
                    {
                        var bestH = horizontalCandidates
                            .OrderBy(s => DistancePointToSegment(quarterCenter, s.A, s.B))
                            .First();
                        var bestV = verticalCandidates
                            .OrderBy(s => DistancePointToSegment(quarterCenter, s.A, s.B))
                            .First();
                        if (TryIntersectInfiniteLines(bestH.A, bestH.B, bestV.A, bestV.B, out var fallback) &&
                            IsPointInExpandedExtents(fallback, quarterExtents, 4.0))
                        {
                            foundTarget = true;
                            target = fallback;
                        }
                    }

                    if (!foundTarget)
                    {
                        skipped++;
                        continue;
                    }

                    foreach (var old in oldLabelEntities)
                    {
                        if (consumedOldLabelIds.Contains(old.Key))
                        {
                            continue;
                        }

                        if (!IsPointInExpandedExtents(old.Value, quarterExtents, quarterTol))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(old.Key, OpenMode.ForWrite, false) is Entity oldEnt) || oldEnt.IsErased)
                        {
                            continue;
                        }

                        oldEnt.Erase();
                        consumedOldLabelIds.Add(old.Key);
                        erased++;
                    }

                    InsertAndExplodeLsdLabelBlock(
                        database,
                        tr,
                        ms,
                        editor: null,
                        info.Quarter,
                        new Point3d(target.X, target.Y, 0.0),
                        "L-SECTION-LSD");
                    placed++;
                }

                tr.Commit();
                if (placed > 0 || erased > 0 || skipped > 0)
                {
                    logger?.WriteLine($"Cleanup: rebuilt {placed} LSD label block(s) at final quarter intersections (erased {erased} stale label entity/ies, skipped {skipped} quarter(s)).");
                }
            }
        }

        private static void SnapQuarterLsdLinesToSectionBoundaries(Database database, IEnumerable<ObjectId> requestedQuarterIds, Logger? logger)
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

                var sectionBoundarySegments = new List<(Point2d A, Point2d B)>();
                var lsdLineIds = new List<ObjectId>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ent is Line)
                        {
                            lsdLineIds.Add(id);
                        }

                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var mid = Midpoint(a, b);
                    if (!IsPointInAnyWindow(mid))
                    {
                        continue;
                    }

                    sectionBoundarySegments.Add((a, b));
                }

                if (sectionBoundarySegments.Count == 0 || lsdLineIds.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var adjusted = 0;
                const double minT = 1e-3;
                const double endpointMoveTol = 0.05;

                foreach (var id in lsdLineIds)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Line ln) || ln.IsErased)
                    {
                        continue;
                    }

                    var p0 = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    var p1 = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (!IsAdjustableLsdLineSegment(p0, p1))
                    {
                        continue;
                    }

                    var center = Midpoint(p0, p1);
                    if (!IsPointInAnyWindow(center))
                    {
                        continue;
                    }

                    var dirVec = p1 - p0;
                    var len = dirVec.Length;
                    if (len <= 1e-4)
                    {
                        continue;
                    }

                    var dir = dirVec / len;
                    var tMin = -0.5 * len;
                    var tMax = 0.5 * len;
                    double? bestNeg = null;
                    double? bestPos = null;

                    foreach (var seg in sectionBoundarySegments)
                    {
                        if (!TryIntersectInfiniteLineWithSegment(center, dir, seg.A, seg.B, out var t))
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

                    var newTMin = bestNeg ?? tMin;
                    var newTMax = bestPos ?? tMax;
                    if (newTMax - newTMin <= minT)
                    {
                        continue;
                    }

                    var newA = center + (dir * newTMin);
                    var newB = center + (dir * newTMax);
                    if (newA.GetDistanceTo(p0) <= endpointMoveTol && newB.GetDistanceTo(p1) <= endpointMoveTol)
                    {
                        continue;
                    }

                    ln.StartPoint = new Point3d(newA.X, newA.Y, ln.StartPoint.Z);
                    ln.EndPoint = new Point3d(newB.X, newB.Y, ln.EndPoint.Z);
                    adjusted++;
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {adjusted} L-SECTION-LSD line(s) to nearest L-SEC/L-USEC boundaries.");
                }
            }
        }

        private static void RecenterExplodedLsdLabelsToFinalLinework(
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

            bool TryGetEntityCenter(Entity ent, out Point2d center)
            {
                center = default;
                if (ent == null)
                {
                    return false;
                }

                try
                {
                    var ext = ent.GeometricExtents;
                    center = new Point2d(
                        0.5 * (ext.MinPoint.X + ext.MaxPoint.X),
                        0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y));
                    return true;
                }
                catch
                {
                    if (ent is DBText dbt)
                    {
                        center = new Point2d(dbt.Position.X, dbt.Position.Y);
                        return true;
                    }

                    if (ent is MText mt)
                    {
                        center = new Point2d(mt.Location.X, mt.Location.Y);
                        return true;
                    }

                    if (ent is BlockReference br)
                    {
                        center = new Point2d(br.Position.X, br.Position.Y);
                        return true;
                    }
                }

                return false;
            }

            bool TryIntersectSegments(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection)
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
                var u = Cross2d(diff, da) / denom;
                if (t < -1e-6 || t > 1.0 + 1e-6 || u < -1e-6 || u > 1.0 + 1e-6)
                {
                    return false;
                }

                intersection = a0 + (da * t);
                return true;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var horizontalLsdSegments = new List<(Point2d A, Point2d B)>();
                var verticalLsdSegments = new List<(Point2d A, Point2d B)>();
                var labelEntityCenters = new Dictionary<ObjectId, Point2d>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryReadOpenSegment(ent, out var a, out var b))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            if (!DoesSegmentIntersectAnyWindow(a, b))
                            {
                                continue;
                            }

                            if (IsHorizontalLike(a, b))
                            {
                                horizontalLsdSegments.Add((a, b));
                            }
                            else if (IsVerticalLike(a, b))
                            {
                                verticalLsdSegments.Add((a, b));
                            }

                            continue;
                        }

                        var shortMid = Midpoint(a, b);
                        if (IsPointInAnyWindow(shortMid))
                        {
                            labelEntityCenters[id] = shortMid;
                        }

                        continue;
                    }

                    if (TryGetEntityCenter(ent, out var center) && IsPointInAnyWindow(center))
                    {
                        labelEntityCenters[id] = center;
                    }
                }

                if (horizontalLsdSegments.Count == 0 ||
                    verticalLsdSegments.Count == 0 ||
                    labelEntityCenters.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var targetCenters = new List<Point2d>();
                var targetDedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var hi = 0; hi < horizontalLsdSegments.Count; hi++)
                {
                    var h = horizontalLsdSegments[hi];
                    for (var vi = 0; vi < verticalLsdSegments.Count; vi++)
                    {
                        var v = verticalLsdSegments[vi];
                        if (!TryIntersectSegments(h.A, h.B, v.A, v.B, out var intersection))
                        {
                            continue;
                        }

                        if (!IsPointInAnyWindow(intersection))
                        {
                            continue;
                        }

                        var key = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:0.###}|{1:0.###}",
                            intersection.X,
                            intersection.Y);
                        if (!targetDedupe.Add(key))
                        {
                            continue;
                        }

                        targetCenters.Add(intersection);
                    }
                }

                if (targetCenters.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double clusterJoinTol = 24.0;
                var remaining = new HashSet<ObjectId>(labelEntityCenters.Keys);
                var clusters = new List<List<ObjectId>>();
                while (remaining.Count > 0)
                {
                    var seed = remaining.First();
                    remaining.Remove(seed);

                    var cluster = new List<ObjectId> { seed };
                    var queue = new Queue<ObjectId>();
                    queue.Enqueue(seed);
                    while (queue.Count > 0)
                    {
                        var current = queue.Dequeue();
                        var currentCenter = labelEntityCenters[current];
                        var nearby = new List<ObjectId>();
                        foreach (var other in remaining)
                        {
                            if (currentCenter.GetDistanceTo(labelEntityCenters[other]) <= clusterJoinTol)
                            {
                                nearby.Add(other);
                            }
                        }

                        for (var i = 0; i < nearby.Count; i++)
                        {
                            var other = nearby[i];
                            remaining.Remove(other);
                            cluster.Add(other);
                            queue.Enqueue(other);
                        }
                    }

                    clusters.Add(cluster);
                }

                if (clusters.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var clusterInfos = new List<(List<ObjectId> Ids, Point2d Center, double ClosestDistance)>();
                for (var i = 0; i < clusters.Count; i++)
                {
                    var cluster = clusters[i];
                    var sumX = 0.0;
                    var sumY = 0.0;
                    for (var j = 0; j < cluster.Count; j++)
                    {
                        var c = labelEntityCenters[cluster[j]];
                        sumX += c.X;
                        sumY += c.Y;
                    }

                    var center = new Point2d(sumX / cluster.Count, sumY / cluster.Count);
                    var nearest = double.MaxValue;
                    for (var ti = 0; ti < targetCenters.Count; ti++)
                    {
                        var d = center.GetDistanceTo(targetCenters[ti]);
                        if (d < nearest)
                        {
                            nearest = d;
                        }
                    }

                    clusterInfos.Add((cluster, center, nearest));
                }

                var orderedClusters = clusterInfos
                    .OrderBy(ci => ci.ClosestDistance)
                    .ToList();

                var usedTargets = new bool[targetCenters.Count];
                const double maxSnapDistance = 80.0;
                const double minMoveTol = 0.05;
                const double ambiguousTargetDeltaTol = 1.5;
                var movedClusters = 0;
                var movedEntities = 0;
                var ambiguousClustersSkipped = 0;
                for (var i = 0; i < orderedClusters.Count; i++)
                {
                    var cluster = orderedClusters[i];
                    var bestTargetIndex = -1;
                    var bestDistance = double.MaxValue;
                    var secondBestDistance = double.MaxValue;
                    for (var ti = 0; ti < targetCenters.Count; ti++)
                    {
                        if (usedTargets[ti])
                        {
                            continue;
                        }

                        var d = cluster.Center.GetDistanceTo(targetCenters[ti]);
                        if (d < bestDistance)
                        {
                            secondBestDistance = bestDistance;
                            bestDistance = d;
                            bestTargetIndex = ti;
                        }
                        else if (d < secondBestDistance)
                        {
                            secondBestDistance = d;
                        }
                    }

                    if (bestTargetIndex < 0 || bestDistance > maxSnapDistance)
                    {
                        continue;
                    }

                    if (secondBestDistance < double.MaxValue &&
                        (secondBestDistance - bestDistance) <= ambiguousTargetDeltaTol)
                    {
                        ambiguousClustersSkipped++;
                        continue;
                    }

                    usedTargets[bestTargetIndex] = true;
                    if (bestDistance <= minMoveTol)
                    {
                        continue;
                    }

                    var target = targetCenters[bestTargetIndex];
                    var displacement = new Vector3d(
                        target.X - cluster.Center.X,
                        target.Y - cluster.Center.Y,
                        0.0);
                    var transform = Matrix3d.Displacement(displacement);
                    var movedAny = false;
                    for (var j = 0; j < cluster.Ids.Count; j++)
                    {
                        var id = cluster.Ids[j];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            writable.TransformBy(transform);
                            movedEntities++;
                            movedAny = true;
                        }
                        catch
                        {
                        }
                    }

                    if (movedAny)
                    {
                        movedClusters++;
                    }
                }

                tr.Commit();
                if (movedClusters > 0)
                {
                    logger?.WriteLine($"Cleanup: recentered {movedClusters} LSD label block cluster(s) to final LSD line intersections ({movedEntities} entity move(s)).");
                }
                if (ambiguousClustersSkipped > 0)
                {
                    logger?.WriteLine($"Cleanup: skipped {ambiguousClustersSkipped} ambiguous LSD label cluster snap(s).");
                }
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

        private static SectionBuildResult DrawSectionFromIndex(
            Editor editor,
            Database database,
            SectionOutline outline,
            SectionKey key,
            bool drawLsds,
            string secType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes = null)
        {
            var quarterIds = new Dictionary<QuarterSelection, ObjectId>();
            var quarterHelperEntityIds = new List<ObjectId>();
            ObjectId sectionLabelId = ObjectId.Null;
            ObjectId sectionId;
            var normalizedSecType = NormalizeSecType(secType);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureSecTypeLayers(database, transaction, normalizedSecType, quarterSecTypes);
                EnsureLayer(database, transaction, "L-QSEC");
                EnsureLayer(database, transaction, "L-QSEC-BOX");
                SetLayerVisibility(database, transaction, "L-QSEC-BOX", isOff: true, isPlottable: false);

                var sectionPolyline = new Polyline(outline.Vertices.Count)
                {
                    Closed = outline.Closed,
                    Layer = "L-QSEC-BOX",
                    ColorIndex = 256
                };

                for (var i = 0; i < outline.Vertices.Count; i++)
                {
                    var vertex = outline.Vertices[i];
                    sectionPolyline.AddVertexAt(i, vertex, 0, 0, 0);
                }

                sectionId = modelSpace.AppendEntity(sectionPolyline);
                transaction.AddNewlyCreatedDBObject(sectionPolyline, true);

                if (TryBuildQuarterMap(sectionPolyline, out var quarterMap, out var anchors))
                {
                    var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                    if (TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) &&
                        TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) &&
                        TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) &&
                        TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
                    {
                        foreach (var id in DrawSectionBoundaryQuarterSegmentPolylines(
                            modelSpace,
                            transaction,
                            nw,
                            ne,
                            sw,
                            se,
                            normalizedSecType,
                            quarterSecTypes,
                            clipToWindow: null,
                            anchors.Top,
                            anchors.Right,
                            anchors.Bottom,
                            anchors.Left))
                        {
                            quarterHelperEntityIds.Add(id);
                        }
                    }

                    foreach (var quarter in quarterMap)
                    {
                        quarter.Value.Layer = "L-QSEC-BOX";
                        quarter.Value.ColorIndex = 256;
                        var quarterId = modelSpace.AppendEntity(quarter.Value);
                        transaction.AddNewlyCreatedDBObject(quarter.Value, true);
                        quarterIds[quarter.Key] = quarterId;
                    }

                    var qv = new Line(new Point3d(anchors.Top.X, anchors.Top.Y, 0), new Point3d(anchors.Bottom.X, anchors.Bottom.Y, 0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var qh = new Line(new Point3d(anchors.Left.X, anchors.Left.Y, 0), new Point3d(anchors.Right.X, anchors.Right.Y, 0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var qvId = modelSpace.AppendEntity(qv);
                    transaction.AddNewlyCreatedDBObject(qv, true);
                    var qhId = modelSpace.AppendEntity(qh);
                    transaction.AddNewlyCreatedDBObject(qh, true);

                    quarterHelperEntityIds.Add(qvId);
                    quarterHelperEntityIds.Add(qhId);

                    if (drawLsds)
                    {
                        DrawLsdSubdivisionLines(
                            database,
                            transaction,
                            modelSpace,
                            editor,
                            quarterMap,
                            anchors,
                            key,
                            normalizedSecType);
                    }

                    var center = new Point3d(
                        0.5 * (anchors.Top.X + anchors.Bottom.X),
                        0.5 * (anchors.Left.Y + anchors.Right.Y),
                        0);
                    sectionLabelId = InsertSectionLabelBlock(modelSpace, blockTable, transaction, editor, center, key);
                }

                transaction.Commit();
            }

            return new SectionBuildResult(sectionId, quarterIds, quarterHelperEntityIds, sectionLabelId);
        }

        private static void EnsureSecTypeLayers(
            Database database,
            Transaction transaction,
            string fallbackSecType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes)
        {
            if (database == null || transaction == null)
            {
                return;
            }

            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeSecType(fallbackSecType)
            };

            if (quarterSecTypes != null)
            {
                foreach (var secType in quarterSecTypes.Values)
                {
                    layers.Add(NormalizeSecType(secType));
                }
            }

            foreach (var layer in layers)
            {
                EnsureLayer(database, transaction, layer);
            }
        }

        private static string ResolveQuarterSecTypeForQuarter(
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes,
            QuarterSelection quarter,
            string fallbackSecType)
        {
            if (quarterSecTypes != null &&
                quarterSecTypes.TryGetValue(quarter, out var secType) &&
                !string.IsNullOrWhiteSpace(secType))
            {
                return NormalizeSecType(secType);
            }

            return NormalizeSecType(fallbackSecType);
        }

        private static void DrawLsdSubdivisionLines(
            Database database,
            Transaction transaction,
            BlockTableRecord modelSpace,
            Editor editor,
            Dictionary<QuarterSelection, Polyline> quarterMap,
            QuarterAnchors sectionAnchors,
            SectionKey key,
            string secType)
        {
            if (quarterMap == null || quarterMap.Count == 0)
            {
                return;
            }

            var lsdLayer = "L-SECTION-LSD";
            EnsureLayer(database, transaction, lsdLayer);

            var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
            var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));

            foreach (var pair in quarterMap)
            {
                var quarterAnchors = GetLsdAnchorsForQuarter(pair.Value, eastUnit, northUnit);

                var verticalStart = quarterAnchors.Top;
                var verticalEnd = quarterAnchors.Bottom;
                var horizontalStart = quarterAnchors.Left;
                var horizontalEnd = quarterAnchors.Right;

                var vertical = new Line(
                    new Point3d(verticalStart.X, verticalStart.Y, 0),
                    new Point3d(verticalEnd.X, verticalEnd.Y, 0))
                {
                    Layer = lsdLayer,
                    ColorIndex = 256
                };
                var horizontal = new Line(
                    new Point3d(horizontalStart.X, horizontalStart.Y, 0),
                    new Point3d(horizontalEnd.X, horizontalEnd.Y, 0))
                {
                    Layer = lsdLayer,
                    ColorIndex = 256
                };

                modelSpace.AppendEntity(vertical);
                transaction.AddNewlyCreatedDBObject(vertical, true);
                modelSpace.AppendEntity(horizontal);
                transaction.AddNewlyCreatedDBObject(horizontal, true);

                var labelCenter = new Point3d(
                    0.25 * (verticalStart.X + verticalEnd.X + horizontalStart.X + horizontalEnd.X),
                    0.25 * (verticalStart.Y + verticalEnd.Y + horizontalStart.Y + horizontalEnd.Y),
                    0.0);
                InsertAndExplodeLsdLabelBlock(database, transaction, modelSpace, editor, pair.Key, labelCenter, lsdLayer);
            }
        }

        private static List<ObjectId> DrawSectionBoundaryQuarterSegmentPolylines(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point2d nw,
            Point2d ne,
            Point2d sw,
            Point2d se,
            string secType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes,
            Extents3d? clipToWindow,
            Point2d? northQuarter = null,
            Point2d? eastQuarter = null,
            Point2d? southQuarter = null,
            Point2d? westQuarter = null)
        {
            var ids = new List<ObjectId>();
            if (modelSpace == null || transaction == null)
            {
                return ids;
            }

            var wMid = westQuarter ?? Midpoint(nw, sw);
            var eMid = eastQuarter ?? Midpoint(ne, se);
            var nMid = northQuarter ?? Midpoint(nw, ne);
            var sMid = southQuarter ?? Midpoint(sw, se);

            var swType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthWest, secType);
            var seType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthEast, secType);
            var neType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthEast, secType);
            var nwType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthWest, secType);

            var segments = new[]
            {
                // Assign each half-edge from its owning quarter to avoid cross-quarter bleed.
                (A: sw, B: sMid, Layer: swType),
                (A: sMid, B: se, Layer: seType),
                (A: se, B: eMid, Layer: seType),
                (A: eMid, B: ne, Layer: neType),
                (A: ne, B: nMid, Layer: neType),
                (A: nMid, B: nw, Layer: nwType),
                (A: nw, B: wMid, Layer: nwType),
                (A: wMid, B: sw, Layer: swType),
            };

            foreach (var seg in segments)
            {
                if (!TryClipSegmentToWindow(seg.A, seg.B, clipToWindow, out var a, out var b))
                {
                    continue;
                }

                var poly = new Polyline(2)
                {
                    Layer = seg.Layer,
                    ColorIndex = 256
                };
                poly.AddVertexAt(0, a, 0, 0, 0);
                poly.AddVertexAt(1, b, 0, 0, 0);
                var id = modelSpace.AppendEntity(poly);
                transaction.AddNewlyCreatedDBObject(poly, true);
                ids.Add(id);
            }

            return ids;
        }

        private static List<ObjectId> DrawSectionBoundaryQuarterSegmentPolylines(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point2d nw,
            Point2d ne,
            Point2d sw,
            Point2d se,
            string secType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes,
            IReadOnlyList<Extents3d> clipWindows,
            Point2d? northQuarter = null,
            Point2d? eastQuarter = null,
            Point2d? southQuarter = null,
            Point2d? westQuarter = null)
        {
            if (clipWindows == null || clipWindows.Count == 0)
            {
                return DrawSectionBoundaryQuarterSegmentPolylines(
                    modelSpace, transaction, nw, ne, sw, se, secType, quarterSecTypes, (Extents3d?)null,
                    northQuarter, eastQuarter, southQuarter, westQuarter);
            }

            var ids = new List<ObjectId>();
            var wMid = westQuarter ?? Midpoint(nw, sw);
            var eMid = eastQuarter ?? Midpoint(ne, se);
            var nMid = northQuarter ?? Midpoint(nw, ne);
            var sMid = southQuarter ?? Midpoint(sw, se);

            var swType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthWest, secType);
            var seType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthEast, secType);
            var neType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthEast, secType);
            var nwType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthWest, secType);

            var segments = new[]
            {
                // Assign each half-edge from its owning quarter to avoid cross-quarter bleed.
                (A: sw, B: sMid, Layer: swType),
                (A: sMid, B: se, Layer: seType),
                (A: se, B: eMid, Layer: seType),
                (A: eMid, B: ne, Layer: neType),
                (A: ne, B: nMid, Layer: neType),
                (A: nMid, B: nw, Layer: nwType),
                (A: nw, B: wMid, Layer: nwType),
                (A: wMid, B: sw, Layer: swType),
            };

            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in segments)
            {
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

                    var poly = new Polyline(2)
                    {
                        Layer = seg.Layer,
                        ColorIndex = 256
                    };
                    poly.AddVertexAt(0, a, 0, 0, 0);
                    poly.AddVertexAt(1, b, 0, 0, 0);
                    var id = modelSpace.AppendEntity(poly);
                    transaction.AddNewlyCreatedDBObject(poly, true);
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static void InsertAndExplodeLsdLabelBlock(
            Database database,
            Transaction transaction,
            BlockTableRecord modelSpace,
            Editor? editor,
            QuarterSelection quarter,
            Point3d position,
            string layerName)
        {
            var preferredName = GetLsdLabelBlockName(quarter);
            if (!TryEnsureLsdBlockLoaded(database, transaction, preferredName, editor))
            {
                editor?.WriteMessage($"\nLSD label load failed: {preferredName}");
            }

            if (!TryResolveLsdLabelBlock(database, transaction, quarter, out var blockId))
            {
                editor?.WriteMessage($"\nLSD label block not found for {quarter}.");
                return;
            }

            var blockRef = new BlockReference(position, blockId)
            {
                ScaleFactors = new Scale3d(1.0),
                Layer = layerName,
                ColorIndex = 256
            };
            modelSpace.AppendEntity(blockRef);
            transaction.AddNewlyCreatedDBObject(blockRef, true);

            var exploded = new DBObjectCollection();
            blockRef.Explode(exploded);
            var explodedEntities = new List<Entity>();
            var haveExtents = false;
            var minX = 0.0;
            var minY = 0.0;
            var maxX = 0.0;
            var maxY = 0.0;
            foreach (DBObject dbObject in exploded)
            {
                if (dbObject is Entity entity)
                {
                    if (!string.IsNullOrWhiteSpace(layerName))
                    {
                        entity.Layer = layerName;
                    }

                    entity.ColorIndex = 256;
                    modelSpace.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    explodedEntities.Add(entity);

                    try
                    {
                        var ext = entity.GeometricExtents;
                        if (!haveExtents)
                        {
                            minX = ext.MinPoint.X;
                            minY = ext.MinPoint.Y;
                            maxX = ext.MaxPoint.X;
                            maxY = ext.MaxPoint.Y;
                            haveExtents = true;
                        }
                        else
                        {
                            minX = Math.Min(minX, ext.MinPoint.X);
                            minY = Math.Min(minY, ext.MinPoint.Y);
                            maxX = Math.Max(maxX, ext.MaxPoint.X);
                            maxY = Math.Max(maxY, ext.MaxPoint.Y);
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    dbObject.Dispose();
                }
            }

            if (haveExtents && explodedEntities.Count > 0)
            {
                var center = new Point3d(
                    0.5 * (minX + maxX),
                    0.5 * (minY + maxY),
                    position.Z);
                var displacement = position - center;
                if (displacement.Length > 1e-4)
                {
                    var transform = Matrix3d.Displacement(displacement);
                    for (var i = 0; i < explodedEntities.Count; i++)
                    {
                        try
                        {
                            explodedEntities[i].TransformBy(transform);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            blockRef.Erase();
            editor?.WriteMessage($"\nLSD label inserted/exploded: {preferredName} ({quarter}).");
        }

        private static bool TryEnsureLsdBlockLoaded(Database database, Transaction transaction, string blockName, Editor? editor)
        {
            if (string.IsNullOrWhiteSpace(blockName))
            {
                return false;
            }

            var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (bt.Has(blockName))
            {
                return true;
            }

            const string blockFolder = @"C:\AUTOCAD-SETUP CG\BLOCKS\_CG BLOCKS";
            var blockPath = Path.Combine(blockFolder, blockName + ".dwg");
            if (!File.Exists(blockPath))
            {
                editor?.WriteMessage($"\nLSD block file missing: {blockPath}");
                return false;
            }

            try
            {
                using (var sourceDb = new Database(false, true))
                {
                    sourceDb.ReadDwgFile(blockPath, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    database.Insert(blockName, sourceDb, false);
                }

                editor?.WriteMessage($"\nLSD block loaded: {blockName}");
                return true;
            }
            catch (System.Exception ex)
            {
                editor?.WriteMessage($"\nLSD block load exception for {blockName}: {ex.Message}");
                return false;
            }
        }

        private static bool TryResolveLsdLabelBlock(
            Database database,
            Transaction transaction,
            QuarterSelection quarter,
            out ObjectId blockId)
        {
            blockId = ObjectId.Null;

            var targetSuffix = quarter switch
            {
                QuarterSelection.NorthWest => "NW",
                QuarterSelection.NorthEast => "NE",
                QuarterSelection.SouthWest => "SW",
                QuarterSelection.SouthEast => "SE",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(targetSuffix))
            {
                return false;
            }

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);

            // Preferred explicit names first.
            var preferred = new[]
            {
                $"label_lsd_{targetSuffix.ToLowerInvariant()}",
                $"LABEL_LSD_{targetSuffix}",
                $"label-lsd-{targetSuffix.ToLowerInvariant()}",
                $"LABEL-LSD-{targetSuffix}"
            };

            foreach (var name in preferred)
            {
                if (blockTable.Has(name))
                {
                    blockId = blockTable[name];
                    return true;
                }
            }

            // Fallback: fuzzy lookup by normalized block name.
            var targetToken = $"LABELLSD{targetSuffix}";
            foreach (ObjectId id in blockTable)
            {
                var btr = transaction.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null || btr.IsAnonymous || btr.IsLayout || btr.IsFromExternalReference)
                {
                    continue;
                }

                var normalized = NormalizeBlockName(btr.Name);
                if (normalized == targetToken || normalized.EndsWith(targetToken, StringComparison.Ordinal))
                {
                    blockId = id;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeBlockName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                }
            }

            return sb.ToString();
        }

        private static string GetLsdLabelBlockName(QuarterSelection quarter)
        {
            switch (quarter)
            {
                case QuarterSelection.NorthWest:
                    return "label_lsd_nw";
                case QuarterSelection.NorthEast:
                    return "label_lsd_ne";
                case QuarterSelection.SouthWest:
                    return "label_lsd_sw";
                case QuarterSelection.SouthEast:
                    return "label_lsd_se";
                default:
                    return string.Empty;
            }
        }

        private static QuarterAnchors GetLsdAnchorsForQuarter(Polyline quarter, Vector2d eastUnit, Vector2d northUnit)
        {
            if (TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) &&
                TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) &&
                TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) &&
                TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
            {
                return new QuarterAnchors(
                    Midpoint(nw, ne), // top
                    Midpoint(sw, se), // bottom
                    Midpoint(nw, sw), // left
                    Midpoint(ne, se)); // right
            }

            // Fallback: quarter extents midpoint anchors.
            return GetFallbackAnchors(quarter);
        }

        private static string NormalizeSecType(string secType)
        {
            return string.Equals(secType?.Trim(), "L-SEC", StringComparison.OrdinalIgnoreCase)
                ? "L-SEC"
                : "L-USEC";
        }

        private static string ResolveSectionType(
            SectionKey key,
            string requestedSecType,
            IReadOnlyDictionary<string, string> inferredSecTypes)
        {
            var keyId = BuildSectionKeyId(key);
            if (inferredSecTypes != null &&
                inferredSecTypes.TryGetValue(keyId, out var inferred) &&
                !string.IsNullOrWhiteSpace(inferred))
            {
                return NormalizeSecType(inferred);
            }

            // "AUTO" or unknown values normalize to L-USEC.
            return NormalizeSecType(requestedSecType);
        }

        private static string BuildSectionQuarterKeyId(SectionKey key, QuarterSelection quarter)
        {
            return BuildSectionQuarterKeyId(BuildSectionKeyId(key), quarter);
        }

        private static string BuildSectionQuarterKeyId(string sectionKeyId, QuarterSelection quarter)
        {
            if (string.IsNullOrWhiteSpace(sectionKeyId))
            {
                return string.Empty;
            }

            var token = QuarterSelectionToToken(quarter);
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            return $"{sectionKeyId}|{token}";
        }

        private static Dictionary<QuarterSelection, string> ResolveQuarterSectionTypes(
            SectionKey key,
            string fallbackSecType,
            IReadOnlyDictionary<string, string> inferredQuarterSecTypes)
        {
            var resolved = new Dictionary<QuarterSelection, string>();
            var fallback = NormalizeSecType(fallbackSecType);
            var sectionQuarterKeys = new[]
            {
                QuarterSelection.NorthWest,
                QuarterSelection.NorthEast,
                QuarterSelection.SouthWest,
                QuarterSelection.SouthEast
            };

            foreach (var quarter in sectionQuarterKeys)
            {
                var quarterKeyId = BuildSectionQuarterKeyId(key, quarter);
                if (!string.IsNullOrWhiteSpace(quarterKeyId) &&
                    inferredQuarterSecTypes != null &&
                    inferredQuarterSecTypes.TryGetValue(quarterKeyId, out var inferred) &&
                    !string.IsNullOrWhiteSpace(inferred))
                {
                    resolved[quarter] = NormalizeSecType(inferred);
                }
                else
                {
                    resolved[quarter] = fallback;
                }
            }

            return resolved;
        }

        private static Dictionary<string, string> InferQuarterSectionTypes(
            IReadOnlyList<SectionRequest> requests,
            IReadOnlyList<string> searchFolders,
            Logger logger)
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requests == null || requests.Count == 0 || searchFolders == null || searchFolders.Count == 0)
            {
                return resolved;
            }

            bool TryGetAtsSectionGridPosition(int section, out int row, out int col)
            {
                row = -1;
                col = -1;
                if (section < 1 || section > 36)
                {
                    return false;
                }

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

            var selectedSectionKeyIds = new HashSet<string>(
                requests.Select(r => BuildSectionKeyId(r.Key)),
                StringComparer.OrdinalIgnoreCase);
            var contextTownshipKeys = BuildContextTownshipKeys(requests);
            var geoms = new List<(
                string KeyId,
                string Label,
                Point2d SW,
                Point2d SE,
                Point2d NW,
                Point2d NE,
                Point2d Center,
                int Zone,
                string Meridian,
                int GlobalX,
                int GlobalY)>();

            foreach (var townshipKey in contextTownshipKeys)
            {
                if (!TryParseTownshipKey(townshipKey, out var zone, out var meridian, out var range, out var township))
                {
                    continue;
                }

                for (var section = 1; section <= 36; section++)
                {
                    var sectionKey = new SectionKey(zone, section.ToString(CultureInfo.InvariantCulture), township, range, meridian);
                    if (!TryLoadSectionOutline(searchFolders, sectionKey, logger, out var outline))
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
                        var globalX = (hasRange && hasGrid) ? ((-rangeNum * 6) + col) : int.MinValue;
                        var globalY = (hasTownship && hasGrid) ? ((townshipNum * 6) + (5 - row)) : int.MinValue;

                        geoms.Add((
                            BuildSectionKeyId(sectionKey),
                            BuildSectionDescriptor(sectionKey),
                            sw, se, nw, ne, center,
                            zone,
                            NormalizeNumberToken(meridian),
                            globalX,
                            globalY));
                    }
                }
            }

            if (geoms.Count == 0)
            {
                return resolved;
            }

            var euVec = geoms[0].SE - geoms[0].SW;
            var nuVec = geoms[0].NW - geoms[0].SW;
            var eu = euVec.Length > 1e-9 ? (euVec / euVec.Length) : new Vector2d(1, 0);
            var nu = nuVec.Length > 1e-9 ? (nuVec / nuVec.Length) : new Vector2d(0, 1);

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

            var selectedOrLocalKeyIds = localKeyIds.Count > 0
                ? localKeyIds
                : selectedSectionKeyIds;
            var quarterGapSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

            void AddQuarterGap(string sectionKeyId, QuarterSelection quarter, double gapMeters)
            {
                if (gapMeters < 1.0)
                {
                    return;
                }

                var quarterKeyId = BuildSectionQuarterKeyId(sectionKeyId, quarter);
                if (string.IsNullOrWhiteSpace(quarterKeyId))
                {
                    return;
                }

                if (!quarterGapSamples.TryGetValue(quarterKeyId, out var samples))
                {
                    samples = new List<double>();
                    quarterGapSamples[quarterKeyId] = samples;
                }

                samples.Add(gapMeters);
            }

            void TryAddQuarterEvidenceFromPair(
                string keyA,
                Point2d a0,
                Point2d a1,
                string keyB,
                Point2d b0,
                Point2d b1,
                bool verticalMode)
            {
                var da = a1 - a0;
                var db = b1 - b0;
                var la = da.Length;
                var lb = db.Length;
                if (la <= 1e-6 || lb <= 1e-6)
                {
                    return;
                }

                var ua = da / la;
                var ub = db / lb;
                if (Math.Abs(ua.DotProduct(ub)) < 0.99)
                {
                    return;
                }

                var axis = ua;
                if (axis.DotProduct(ub) < 0.0)
                {
                    ub = -ub;
                }

                if ((a1 - a0).DotProduct(axis) < 0.0)
                {
                    var tmp = a0;
                    a0 = a1;
                    a1 = tmp;
                    da = a1 - a0;
                    la = da.Length;
                    ua = da / la;
                }

                if ((b1 - b0).DotProduct(axis) < 0.0)
                {
                    var tmp = b0;
                    b0 = b1;
                    b1 = tmp;
                    db = b1 - b0;
                    lb = db.Length;
                    ub = db / lb;
                }

                var aMid = Midpoint(a0, a1);
                var bMid = Midpoint(b0, b1);
                var pa = verticalMode
                    ? (aMid.X * eu.X + aMid.Y * eu.Y)
                    : (aMid.X * nu.X + aMid.Y * nu.Y);
                var pb = verticalMode
                    ? (bMid.X * eu.X + bMid.Y * eu.Y)
                    : (bMid.X * nu.X + bMid.Y * nu.Y);

                var westOrSouth0 = pa <= pb ? a0 : b0;
                var westOrSouth1 = pa <= pb ? a1 : b1;
                var eastOrNorth0 = pa <= pb ? b0 : a0;
                var eastOrNorth1 = pa <= pb ? b1 : a1;
                var baseKey = pa <= pb ? keyA : keyB;
                var otherKey = pa <= pb ? keyB : keyA;

                var baseDir = westOrSouth1 - westOrSouth0;
                var baseLen = baseDir.Length;
                if (baseLen <= 1e-6)
                {
                    return;
                }

                var baseU = baseDir / baseLen;
                var tBase0 = 0.0;
                var tBase1 = baseLen;
                var tOther0 = (eastOrNorth0 - westOrSouth0).DotProduct(baseU);
                var tOther1 = (eastOrNorth1 - westOrSouth0).DotProduct(baseU);
                var overlapMin = Math.Max(Math.Min(tBase0, tBase1), Math.Min(tOther0, tOther1));
                var overlapMax = Math.Min(Math.Max(tBase0, tBase1), Math.Max(tOther0, tOther1));
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
                var minEdgeLength = Math.Min(baseLen, lb);
                if (overlapLength < Math.Max(100.0, minEdgeLength * 0.75))
                {
                    return;
                }

                var baseStart = westOrSouth0 + (baseU * overlapMin);
                var baseEnd = westOrSouth0 + (baseU * overlapMax);

                var otherDir = eastOrNorth1 - eastOrNorth0;
                var otherLen2 = otherDir.DotProduct(otherDir);
                if (otherLen2 <= 1e-9)
                {
                    return;
                }

                double MeasureGap(Point2d baseSample)
                {
                    var proj = (baseSample - eastOrNorth0).DotProduct(otherDir) / otherLen2;
                    proj = Math.Max(0.0, Math.Min(1.0, proj));
                    var otherSample = eastOrNorth0 + (otherDir * proj);

                    var normal = new Vector2d(-baseU.Y, baseU.X);
                    if ((otherSample - baseSample).DotProduct(normal) < 0.0)
                    {
                        normal = -normal;
                    }

                    return Math.Abs((otherSample - baseSample).DotProduct(normal));
                }

                var splitT = overlapMin + (0.5 * (overlapMax - overlapMin));
                splitT = Math.Max(overlapMin, Math.Min(overlapMax, splitT));
                var baseMid = westOrSouth0 + (baseU * splitT);
                var span = overlapMax - overlapMin;
                var sampleQ1 = westOrSouth0 + (baseU * (overlapMin + (0.25 * span)));
                var sampleQ3 = westOrSouth0 + (baseU * (overlapMin + (0.75 * span)));
                var gapQ1 = MeasureGap(sampleQ1);
                var gapQ3 = MeasureGap(sampleQ3);

                var segments = new[]
                {
                    (
                        Gap: gapQ1,
                        BaseQuarter: verticalMode ? QuarterSelection.SouthEast : QuarterSelection.NorthWest,
                        OtherQuarter: verticalMode ? QuarterSelection.SouthWest : QuarterSelection.SouthWest),
                    (
                        Gap: gapQ3,
                        BaseQuarter: verticalMode ? QuarterSelection.NorthEast : QuarterSelection.NorthEast,
                        OtherQuarter: verticalMode ? QuarterSelection.NorthWest : QuarterSelection.SouthEast)
                };

                foreach (var seg in segments)
                {
                    AddQuarterGap(baseKey, seg.BaseQuarter, seg.Gap);
                    AddQuarterGap(otherKey, seg.OtherQuarter, seg.Gap);
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
                (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY) a,
                (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY) b)
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

                if (TryGetNeighborIndex(a.Zone, a.Meridian, a.GlobalX, a.GlobalY, 1, 0, out var eastNeighborIndex))
                {
                    var b = geoms[eastNeighborIndex];
                    if (IsNeighborPairEligible(a, b))
                    {
                        var aIsWest = a.Center.X <= b.Center.X;
                        var west = aIsWest ? a : b;
                        var east = aIsWest ? b : a;
                        TryAddQuarterEvidenceFromPair(
                            west.KeyId,
                            west.SE,
                            west.NE,
                            east.KeyId,
                            east.SW,
                            east.NW,
                            verticalMode: true);
                    }
                }

                if (TryGetNeighborIndex(a.Zone, a.Meridian, a.GlobalX, a.GlobalY, 0, 1, out var northNeighborIndex))
                {
                    var b = geoms[northNeighborIndex];
                    if (IsNeighborPairEligible(a, b))
                    {
                        var aIsSouth = a.Center.Y <= b.Center.Y;
                        var south = aIsSouth ? a : b;
                        var north = aIsSouth ? b : a;
                        TryAddQuarterEvidenceFromPair(
                            south.KeyId,
                            south.NW,
                            south.NE,
                            north.KeyId,
                            north.SW,
                            north.SE,
                            verticalMode: false);
                    }
                }
            }

            foreach (var pair in quarterGapSamples)
            {
                resolved[pair.Key] = InferQuarterSecTypeFromRoadAllowance(pair.Value);
            }

            logger?.WriteLine($"Quarter SEC TYPE inferred: {resolved.Count} quarter(s).");
            return resolved;
        }

        private static Dictionary<string, string> InferSectionTypes(
            IReadOnlyList<SectionRequest> requests,
            IReadOnlyList<string> searchFolders,
            Logger logger)
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requests == null || requests.Count == 0)
            {
                return resolved;
            }

            var uniqueTownships = BuildContextTownshipKeys(requests).ToList();
            var entries = new List<(SectionKey Key, SectionSpatialInfo Info)>();
            try
            {
                foreach (var townshipKey in uniqueTownships)
                {
                    if (!TryParseTownshipKey(townshipKey, out var zone, out var meridian, out var range, out var township))
                    {
                        continue;
                    }

                    for (var section = 1; section <= 36; section++)
                    {
                        var key = new SectionKey(zone, section.ToString(CultureInfo.InvariantCulture), township, range, meridian);
                        if (!TryLoadSectionOutline(searchFolders, key, logger, out var outline))
                        {
                            continue;
                        }

                        if (TryCreateSectionSpatialInfo(outline, section, out var info))
                        {
                            entries.Add((key, info));
                        }
                    }
                }

                if (entries.Count == 0)
                {
                    return resolved;
                }

                foreach (var entry in entries)
                {
                    var peerInfos = entries
                        .Where(e => e.Info != null &&
                                    e.Key.Zone == entry.Key.Zone &&
                                    string.Equals(NormalizeNumberToken(e.Key.Meridian), NormalizeNumberToken(entry.Key.Meridian), StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.Info)
                        .ToList();

                    var haveEastGap = TryMeasureRoadAllowanceGap(entry.Info, peerInfos, eastDirection: true, out var eastGap);
                    var haveSouthGap = TryMeasureRoadAllowanceGap(entry.Info, peerInfos, eastDirection: false, out var southGap);

                    var inferred = InferSecTypeFromRoadAllowance(haveEastGap ? eastGap : (double?)null, haveSouthGap ? southGap : (double?)null);
                    var keyId = BuildSectionKeyId(entry.Key);
                    resolved[keyId] = inferred;

                    if (EnableRoadAllowanceDiagnostics)
                    {
                        logger?.WriteLine(
                            $"SEC TYPE inferred: {keyId} -> {inferred} (eastGap={(haveEastGap ? eastGap.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")}, southGap={(haveSouthGap ? southGap.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")})");
                    }
                }
            }
            finally
            {
                DisposeSectionInfos(entries.Select(e => e.Info).ToList());
            }

            return resolved;
        }

        private static string InferSecTypeFromRoadAllowance(double? eastGap, double? southGap)
        {
            var gaps = new List<double>();
            if (eastGap.HasValue) gaps.Add(eastGap.Value);
            if (southGap.HasValue) gaps.Add(southGap.Value);

            // Blind/correction lines can produce near-zero or overlap gaps; treat as unknown.
            var measurableGaps = gaps.Where(g => g >= 1.0).ToList();
            if (measurableGaps.Count == 0)
            {
                return "L-USEC";
            }

            var hasUsec = measurableGaps.Any(g => Math.Abs(g - RoadAllowanceUsecWidthMeters) <= RoadAllowanceWidthToleranceMeters);
            var hasSec = measurableGaps.Any(g => Math.Abs(g - RoadAllowanceSecWidthMeters) <= RoadAllowanceWidthToleranceMeters);

            if (hasUsec && !hasSec)
            {
                return "L-USEC";
            }

            if (hasSec && !hasUsec)
            {
                return "L-SEC";
            }

            if (hasSec && hasUsec)
            {
                // Prefer surveyed when evidence is mixed so shared 20.11 boundaries
                // on adjacent sections resolve to the same section layer.
                return "L-SEC";
            }

            var nearestUsec = measurableGaps.Min(g => Math.Abs(g - RoadAllowanceUsecWidthMeters));
            var nearestSec = measurableGaps.Min(g => Math.Abs(g - RoadAllowanceSecWidthMeters));
            return nearestSec < nearestUsec ? "L-SEC" : "L-USEC";
        }

        private static string InferQuarterSecTypeFromRoadAllowance(IReadOnlyCollection<double> gaps)
        {
            if (gaps == null || gaps.Count == 0)
            {
                return "L-USEC";
            }

            var measurableGaps = gaps.Where(g => g >= 1.0).ToList();
            if (measurableGaps.Count == 0)
            {
                return "L-USEC";
            }

            var secMatches = measurableGaps
                .Where(g => Math.Abs(g - RoadAllowanceSecWidthMeters) <= RoadAllowanceWidthToleranceMeters)
                .ToList();
            var usecMatches = measurableGaps
                .Where(g => Math.Abs(g - RoadAllowanceUsecWidthMeters) <= RoadAllowanceGapOffsetToleranceMeters)
                .ToList();

            if (secMatches.Count > 0 && usecMatches.Count == 0)
            {
                return "L-SEC";
            }

            if (usecMatches.Count > 0 && secMatches.Count == 0)
            {
                return "L-USEC";
            }

            if (secMatches.Count > 0 && usecMatches.Count > 0)
            {
                if (secMatches.Count > usecMatches.Count)
                {
                    return "L-SEC";
                }

                if (usecMatches.Count > secMatches.Count)
                {
                    return "L-USEC";
                }

                var nearestSecMatched = secMatches.Min(g => Math.Abs(g - RoadAllowanceSecWidthMeters));
                var nearestUsecMatched = usecMatches.Min(g => Math.Abs(g - RoadAllowanceUsecWidthMeters));
                if (nearestSecMatched < nearestUsecMatched)
                {
                    return "L-SEC";
                }

                if (nearestUsecMatched < nearestSecMatched)
                {
                    return "L-USEC";
                }

                // When quarter evidence is evenly mixed, keep unsurveyed to avoid
                // promoting an entire 1/4 to surveyed from only one matching edge.
                return "L-USEC";
            }

            var nearestUsec = measurableGaps.Min(g => Math.Abs(g - RoadAllowanceUsecWidthMeters));
            var nearestSec = measurableGaps.Min(g => Math.Abs(g - RoadAllowanceSecWidthMeters));
            return nearestSec < nearestUsec ? "L-SEC" : "L-USEC";
        }

        private static bool TryMeasureRoadAllowanceGap(
            SectionSpatialInfo source,
            IReadOnlyList<SectionSpatialInfo> townshipInfos,
            bool eastDirection,
            out double gapMeters)
        {
            gapMeters = 0.0;
            if (source == null || townshipInfos == null || townshipInfos.Count == 0)
            {
                return false;
            }

            var sourceCenter = GetSectionCenter(source);
            var bestGap = double.MaxValue;
            var found = false;

            foreach (var candidate in townshipInfos)
            {
                if (candidate == null || ReferenceEquals(candidate, source))
                {
                    continue;
                }

                var candidateCenter = GetSectionCenter(candidate);
                var delta = candidateCenter - sourceCenter;
                var eastDelta = delta.DotProduct(source.EastUnit);
                var northDelta = delta.DotProduct(source.NorthUnit);

                if (eastDirection)
                {
                    if (Math.Abs(northDelta) > Math.Max(source.Height, candidate.Height) * 0.60)
                        continue;

                    var projectedGap = Math.Abs(eastDelta) - (source.Width * 0.5) - (candidate.Width * 0.5);
                    if (projectedGap < -RoadAllowanceWidthToleranceMeters)
                        continue;
                    // Ignore near-zero/overlap artifacts from correction-line geometry.
                    if (projectedGap < 1.0)
                        continue;

                    if (!found || projectedGap < bestGap)
                    {
                        bestGap = projectedGap;
                        found = true;
                    }
                }
                else
                {
                    if (Math.Abs(eastDelta) > Math.Max(source.Width, candidate.Width) * 0.60)
                        continue;

                    var projectedGap = Math.Abs(northDelta) - (source.Height * 0.5) - (candidate.Height * 0.5);
                    if (projectedGap < -RoadAllowanceWidthToleranceMeters)
                        continue;
                    // Ignore near-zero/overlap artifacts from correction-line geometry.
                    if (projectedGap < 1.0)
                        continue;

                    if (!found || projectedGap < bestGap)
                    {
                        bestGap = projectedGap;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            gapMeters = Math.Max(0.0, bestGap);
            return true;
        }

        private static Point2d GetSectionCenter(SectionSpatialInfo section)
        {
            return section.SouthWest +
                   (section.EastUnit * (section.Width * 0.5)) +
                   (section.NorthUnit * (section.Height * 0.5));
        }

        private static void DrawBufferedQuarterWindowsOnDefpoints(
            Database database,
            IEnumerable<ObjectId> quarterIds,
            double buffer,
            Logger? logger)
        {
            if (database == null || quarterIds == null)
            {
                return;
            }

            var quarterIdList = quarterIds.Distinct().ToList();
            logger?.WriteLine($"DEFPOINTS BUFFER: requested quarter ids = {quarterIdList.Count}, buffer={buffer:0.###}m");
            var offsetPolylines = BuildBufferedQuarterOffsetPolylines(database, quarterIdList, buffer, logger);
            if (offsetPolylines.Count == 0)
            {
                logger?.WriteLine("DEFPOINTS BUFFER: no offset polylines created.");
                return;
            }

            List<Polyline> unionBoundaries;
            try
            {
                unionBoundaries = BuildUnionBoundaries(offsetPolylines, logger);
            }
            finally
            {
                foreach (var poly in offsetPolylines)
                {
                    poly.Dispose();
                }
            }

            if (unionBoundaries.Count == 0)
            {
                logger?.WriteLine("DEFPOINTS BUFFER: union produced 0 boundaries.");
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureDefpointsLayer(database, tr);
                ClearPreviousBufferedDefpointsWindows(tr);

                var count = 0;
                var newWindowIds = new List<ObjectId>();
                foreach (var boundary in unionBoundaries)
                {
                    try
                    {
                        logger?.WriteLine($"DEFPOINTS BUFFER: draw boundary area={Math.Abs(boundary.Area):0.###} verts={boundary.NumberOfVertices}");
                    }
                    catch
                    {
                    }

                    boundary.Layer = "DEFPOINTS";
                    boundary.ColorIndex = 8;
                    var windowId = ms.AppendEntity(boundary);
                    tr.AddNewlyCreatedDBObject(boundary, true);
                    newWindowIds.Add(windowId);
                    count++;
                }

                tr.Commit();
                lock (BufferedDefpointsWindowLock)
                {
                    BufferedDefpointsWindowIds.Clear();
                    foreach (var id in newWindowIds)
                    {
                        if (!id.IsNull)
                        {
                            BufferedDefpointsWindowIds.Add(id);
                        }
                    }
                }
                logger?.WriteLine($"Buffered DEFPOINTS windows drawn: {count} outline(s).");
            }

            foreach (var boundary in unionBoundaries)
            {
                if (!boundary.IsDisposed && boundary.Database == null)
                {
                    boundary.Dispose();
                }
            }
        }

        private static void ClearPreviousBufferedDefpointsWindows(Transaction tr)
        {
            if (tr == null)
            {
                return;
            }

            List<ObjectId> previousIds;
            lock (BufferedDefpointsWindowLock)
            {
                previousIds = BufferedDefpointsWindowIds
                    .Where(id => !id.IsNull)
                    .Distinct()
                    .ToList();
                BufferedDefpointsWindowIds.Clear();
            }

            foreach (var id in previousIds)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    {
                        entity.Erase();
                    }
                }
                catch
                {
                }
            }
        }

        private static void ExtendQuarterLinesFromUsecWestSouthToNextUsec(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
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

                    // Accept multi-vertex open polylines only when effectively collinear.
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

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var usecBoundarySegments = new List<(Point2d A, Point2d B)>();
                var sourceVerticalUsecSegments = new List<(Point2d A, Point2d B)>();
                var secTargetSegments = new List<(Point2d A, Point2d B)>();
                var generatedVerticalUsecTargets = new List<(Point2d A, Point2d B)>();
                var qsecLineIds = new List<ObjectId>();
                var lsdLineIds = new List<ObjectId>();
                var qsecVerticalSegments = new List<(Point2d A, Point2d B)>();
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

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        lsdLineIds.Add(id);
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsVerticalLike(a, b))
                        {
                            qsecVerticalSegments.Add((a, b));
                        }

                        qsecLineIds.Add(id);
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        secTargetSegments.Add((a, b));
                        var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        var isSecLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                        if ((isUsecLayer || isSecLayer) && IsVerticalLike(a, b))
                        {
                            generatedVerticalUsecTargets.Add((a, b));
                        }

                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        usecBoundarySegments.Add((a, b));
                        if (IsVerticalLike(a, b))
                        {
                            sourceVerticalUsecSegments.Add((a, b));
                        }
                    }
                }

                if (qsecLineIds.Count == 0 || secTargetSegments.Count == 0 || usecBoundarySegments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double touchTol = 0.20;
                const double minExtend = 0.05;
                const double maxExtend = 40.0;
                const double endpointMoveTol = 0.05;
                var adjusted = 0;
                var horizontalQsecMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var verticalQsecMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();

                foreach (var id in qsecLineIds)
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var p0, out var p1))
                    {
                        continue;
                    }

                    var lineDir = p1 - p0;
                    if (lineDir.Length <= 1e-4)
                    {
                        continue;
                    }

                    var isVerticalQsec = IsVerticalLike(p0, p1);
                    // Apply explicit rule:
                    // - Vertical 1/4 line: extend only S.1/4 endpoint (south end)
                    // - Horizontal 1/4 line: extend only W.1/4 endpoint (west end)
                    var selectedOriginal = p0;
                    var other = p1;
                    if (isVerticalQsec)
                    {
                        if (p1.Y < p0.Y)
                        {
                            selectedOriginal = p1;
                            other = p0;
                        }
                    }
                    else
                    {
                        if (p1.X < p0.X)
                        {
                            selectedOriginal = p1;
                            other = p0;
                        }
                    }

                    var touchesRelevantUsec = false;
                    for (var si = 0; si < usecBoundarySegments.Count; si++)
                    {
                        var boundary = usecBoundarySegments[si];
                        if (isVerticalQsec && !IsHorizontalLike(boundary.A, boundary.B))
                        {
                            continue;
                        }

                        if (!isVerticalQsec && !IsVerticalLike(boundary.A, boundary.B))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(selectedOriginal, boundary.A, boundary.B) <= touchTol)
                        {
                            touchesRelevantUsec = true;
                            break;
                        }
                    }

                    if (!touchesRelevantUsec)
                    {
                        continue;
                    }

                    var outward = selectedOriginal - other;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        continue;
                    }

                    var outwardDir = outward / outwardLen;
                    double? bestTargetDistance = null;
                    for (var ti = 0; ti < secTargetSegments.Count; ti++)
                    {
                        var target = secTargetSegments[ti];
                        if (isVerticalQsec && !IsHorizontalLike(target.A, target.B))
                        {
                            continue;
                        }

                        if (!isVerticalQsec && !IsVerticalLike(target.A, target.B))
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLineWithSegment(selectedOriginal, outwardDir, target.A, target.B, out var t))
                        {
                            continue;
                        }

                        if (t <= minExtend || t > maxExtend)
                        {
                            continue;
                        }

                        if (!bestTargetDistance.HasValue || t < bestTargetDistance.Value)
                        {
                            bestTargetDistance = t;
                        }
                    }

                    if (!bestTargetDistance.HasValue)
                    {
                        continue;
                    }

                    var selectedNew = selectedOriginal + (outwardDir * bestTargetDistance.Value);
                    if (selectedNew.GetDistanceTo(selectedOriginal) <= endpointMoveTol)
                    {
                        continue;
                    }

                    if (writable is Line ln)
                    {
                        if (selectedOriginal.GetDistanceTo(p0) <= selectedOriginal.GetDistanceTo(p1))
                        {
                            ln.StartPoint = new Point3d(selectedNew.X, selectedNew.Y, ln.StartPoint.Z);
                        }
                        else
                        {
                            ln.EndPoint = new Point3d(selectedNew.X, selectedNew.Y, ln.EndPoint.Z);
                        }
                    }
                    else if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        if (selectedOriginal.GetDistanceTo(p0) <= selectedOriginal.GetDistanceTo(p1))
                        {
                            pl.SetPointAt(0, selectedNew);
                        }
                        else
                        {
                            pl.SetPointAt(1, selectedNew);
                        }
                    }
                    else
                    {
                        continue;
                    }

                    adjusted++;
                    if (!isVerticalQsec)
                    {
                        var centerAnchor = other;
                        var towardCenterVec = other - selectedOriginal;
                        var towardCenterLen = towardCenterVec.Length;
                        if (towardCenterLen > 1e-6 && qsecVerticalSegments.Count > 0)
                        {
                            var towardCenterDir = towardCenterVec / towardCenterLen;
                            double? bestCenterT = null;
                            const double centerEndpointTol = 1.0;
                            for (var vi = 0; vi < qsecVerticalSegments.Count; vi++)
                            {
                                var vseg = qsecVerticalSegments[vi];
                                if (!TryIntersectInfiniteLineWithSegment(selectedOriginal, towardCenterDir, vseg.A, vseg.B, out var tCenter))
                                {
                                    continue;
                                }

                                if (tCenter <= centerEndpointTol || tCenter >= (towardCenterLen - centerEndpointTol))
                                {
                                    continue;
                                }

                                if (!bestCenterT.HasValue || tCenter < bestCenterT.Value)
                                {
                                    bestCenterT = tCenter;
                                }
                            }

                            if (bestCenterT.HasValue)
                            {
                                centerAnchor = selectedOriginal + (towardCenterDir * bestCenterT.Value);
                            }
                        }

                        horizontalQsecMidpointAdjustments.Add((
                            selectedOriginal,
                            centerAnchor,
                            Midpoint(selectedOriginal, centerAnchor),
                            Midpoint(selectedNew, centerAnchor)));
                    }
                    else
                    {
                        verticalQsecMidpointAdjustments.Add((
                            selectedOriginal,
                            other,
                            Midpoint(selectedOriginal, other),
                            Midpoint(selectedNew, other)));
                    }
                }

                var lsdAdjusted = 0;
                if (horizontalQsecMidpointAdjustments.Count > 0 && lsdLineIds.Count > 0)
                {
                    const double lsdOnOldQsecTol = 0.35;
                    const double lsdMaxMove = 25.0;

                    bool TryMoveLsdEndpointByIndex(Entity writableLsd, int endpointIndex, Point2d target)
                    {
                        if (endpointIndex != 0 && endpointIndex != 1)
                        {
                            return false;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            var old = endpointIndex == 0
                                ? new Point2d(lsdLine.StartPoint.X, lsdLine.StartPoint.Y)
                                : new Point2d(lsdLine.EndPoint.X, lsdLine.EndPoint.Y);
                            if (old.GetDistanceTo(target) <= endpointMoveTol)
                            {
                                return false;
                            }

                            if (endpointIndex == 0)
                            {
                                lsdLine.StartPoint = new Point3d(target.X, target.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(target.X, target.Y, lsdLine.EndPoint.Z);
                            }

                            return true;
                        }

                        if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices >= 2)
                        {
                            var index = endpointIndex == 0 ? 0 : lsdPoly.NumberOfVertices - 1;
                            var old = lsdPoly.GetPoint2dAt(index);
                            if (old.GetDistanceTo(target) <= endpointMoveTol)
                            {
                                return false;
                            }

                            lsdPoly.SetPointAt(index, target);
                            return true;
                        }

                        return false;
                    }

                    bool TryMappedMidpoint(Point2d endpoint, out Point2d mappedMid, out double bestSegDistance, out double bestMidDistance)
                    {
                        mappedMid = endpoint;
                        bestSegDistance = double.MaxValue;
                        bestMidDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;

                        for (var i = 0; i < horizontalQsecMidpointAdjustments.Count; i++)
                        {
                            var adj = horizontalQsecMidpointAdjustments[i];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOnOldQsecTol)
                            {
                                continue;
                            }

                            var midDistance = endpoint.GetDistanceTo(adj.OldMid);
                            var moveDistance = endpoint.GetDistanceTo(adj.NewMid);
                            if (moveDistance <= endpointMoveTol || moveDistance > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && midDistance < (bestMidDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(midDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && moveDistance < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestMidDistance = midDistance;
                            bestMoveDistance = moveDistance;
                            mappedMid = adj.NewMid;
                        }

                        return bestSegDistance < double.MaxValue;
                    }

                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryMappedMidpoint(p0, out var mid0, out var seg0, out var md0);
                        var has1 = TryMappedMidpoint(p1, out var mid1, out var seg1, out var md1);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var move0 = has0;
                        var move1 = has1;
                        if (move0 && move1 && mid0.GetDistanceTo(mid1) < (MinAdjustableLsdLineLengthMeters * 0.60))
                        {
                            if (seg1 < seg0 || (Math.Abs(seg1 - seg0) <= 1e-6 && md1 < md0))
                            {
                                move0 = false;
                            }
                            else
                            {
                                move1 = false;
                            }
                        }

                        if (move0 && TryMoveLsdEndpointByIndex(writableLsd, 0, mid0))
                        {
                            lsdAdjusted++;
                        }

                        if (move1 && TryMoveLsdEndpointByIndex(writableLsd, 1, mid1))
                        {
                            lsdAdjusted++;
                        }
                    }
                }

                var southQuarterLsdAdjusted = 0;
                if (verticalQsecMidpointAdjustments.Count > 0 && lsdLineIds.Count > 0)
                {
                    const double lsdOnOldQsecTol = 0.35;
                    const double lsdMaxMove = 28.0;

                    bool TryMoveLsdEndpointByIndex(Entity writableLsd, int endpointIndex, Point2d target)
                    {
                        if (endpointIndex != 0 && endpointIndex != 1)
                        {
                            return false;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            var old = endpointIndex == 0
                                ? new Point2d(lsdLine.StartPoint.X, lsdLine.StartPoint.Y)
                                : new Point2d(lsdLine.EndPoint.X, lsdLine.EndPoint.Y);
                            if (old.GetDistanceTo(target) <= endpointMoveTol)
                            {
                                return false;
                            }

                            if (endpointIndex == 0)
                            {
                                lsdLine.StartPoint = new Point3d(target.X, target.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(target.X, target.Y, lsdLine.EndPoint.Z);
                            }

                            return true;
                        }

                        if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices >= 2)
                        {
                            var index = endpointIndex == 0 ? 0 : lsdPoly.NumberOfVertices - 1;
                            var old = lsdPoly.GetPoint2dAt(index);
                            if (old.GetDistanceTo(target) <= endpointMoveTol)
                            {
                                return false;
                            }

                            lsdPoly.SetPointAt(index, target);
                            return true;
                        }

                        return false;
                    }

                    bool TryMappedSouthMidpoint(Point2d endpoint, out Point2d mappedMid, out double bestSegDistance, out double bestMidDistance)
                    {
                        mappedMid = endpoint;
                        bestSegDistance = double.MaxValue;
                        bestMidDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;

                        for (var i = 0; i < verticalQsecMidpointAdjustments.Count; i++)
                        {
                            var adj = verticalQsecMidpointAdjustments[i];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOnOldQsecTol)
                            {
                                continue;
                            }

                            var midDistance = endpoint.GetDistanceTo(adj.OldMid);
                            var moveDistance = endpoint.GetDistanceTo(adj.NewMid);
                            if (moveDistance <= endpointMoveTol || moveDistance > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && midDistance < (bestMidDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(midDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && moveDistance < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestMidDistance = midDistance;
                            bestMoveDistance = moveDistance;
                            mappedMid = adj.NewMid;
                        }

                        return bestSegDistance < double.MaxValue;
                    }

                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1) || !IsHorizontalLike(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryMappedSouthMidpoint(p0, out var mid0, out var seg0, out var md0);
                        var has1 = TryMappedSouthMidpoint(p1, out var mid1, out var seg1, out var md1);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var move0 = has0;
                        var move1 = has1;
                        if (move0 && move1 && mid0.GetDistanceTo(mid1) < (MinAdjustableLsdLineLengthMeters * 0.60))
                        {
                            if (seg1 < seg0 || (Math.Abs(seg1 - seg0) <= 1e-6 && md1 < md0))
                            {
                                move0 = false;
                            }
                            else
                            {
                                move1 = false;
                            }
                        }

                        if (move0 && TryMoveLsdEndpointByIndex(writableLsd, 0, mid0))
                        {
                            southQuarterLsdAdjusted++;
                        }

                        if (move1 && TryMoveLsdEndpointByIndex(writableLsd, 1, mid1))
                        {
                            southQuarterLsdAdjusted++;
                        }
                    }
                }

                var westHalfLsdAdjusted = 0;
                if (lsdLineIds.Count > 0 && sourceVerticalUsecSegments.Count > 0 && generatedVerticalUsecTargets.Count > 0)
                {
                    for (var i = 0; i < lsdLineIds.Count; i++)
                    {
                        var id = lsdLineIds[i];
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1) || !IsHorizontalLike(p0, p1))
                        {
                            continue;
                        }

                        var west = p0;
                        var east = p1;
                        if (east.X < west.X)
                        {
                            var tmp = west;
                            west = east;
                            east = tmp;
                        }

                        // Strict source gate: only LSD endpoints anchored on original L-USEC (30.18) boundaries.
                        var touchesOriginalUsec = false;
                        for (var si = 0; si < sourceVerticalUsecSegments.Count; si++)
                        {
                            var src = sourceVerticalUsecSegments[si];
                            if (DistancePointToSegment(west, src.A, src.B) <= touchTol)
                            {
                                touchesOriginalUsec = true;
                                break;
                            }
                        }

                        if (!touchesOriginalUsec)
                        {
                            continue;
                        }

                        var outward = west - east;
                        var outwardLen = outward.Length;
                        if (outwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var outwardDir = outward / outwardLen;
                        double? bestTargetDistance = null;
                        for (var ti = 0; ti < generatedVerticalUsecTargets.Count; ti++)
                        {
                            var target = generatedVerticalUsecTargets[ti];
                            if (!TryIntersectInfiniteLineWithSegment(west, outwardDir, target.A, target.B, out var t))
                            {
                                continue;
                            }

                            if (t <= minExtend || t > maxExtend)
                            {
                                continue;
                            }

                            if (!bestTargetDistance.HasValue || t < bestTargetDistance.Value)
                            {
                                bestTargetDistance = t;
                            }
                        }

                        if (!bestTargetDistance.HasValue)
                        {
                            continue;
                        }

                        var westNew = west + (outwardDir * bestTargetDistance.Value);
                        if (westNew.GetDistanceTo(west) <= endpointMoveTol)
                        {
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            var d0 = west.GetDistanceTo(p0);
                            var d1 = west.GetDistanceTo(p1);
                            if (d0 <= d1)
                            {
                                lsdLine.StartPoint = new Point3d(westNew.X, westNew.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(westNew.X, westNew.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            var d0 = west.GetDistanceTo(p0);
                            var d1 = west.GetDistanceTo(p1);
                            lsdPoly.SetPointAt(d0 <= d1 ? 0 : 1, westNew);
                        }
                        else
                        {
                            continue;
                        }

                        westHalfLsdAdjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: extended {adjusted} L-QSEC W.1/4/S.1/4 endpoint(s) to next L-USEC line.");
                }
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of W.1/4 L-QSEC extension line(s).");
                }
                if (southQuarterLsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {southQuarterLsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of S.1/4 L-QSEC extension line(s).");
                }
                if (westHalfLsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: extended {westHalfLsdAdjusted} W.1/2 E-W L-SECTION-LSD endpoint(s) from original L-USEC to generated 20.12 boundary.");
                }
            }
        }

        private static void ExtendSouthBoundarySwQuarterWestToNextUsec(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
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

            bool Near(Point2d p, Point2d q, double tol)
            {
                return p.GetDistanceTo(q) <= tol;
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var sourceSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool IsUsec)>();
                var verticalUsecBoundaries = new List<(Point2d A, Point2d B)>();
                var generatedUsecVerticalTargets = new List<(Point2d A, Point2d B)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
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

                    if (generatedSet.Contains(id))
                    {
                        var isUsecGeneratedLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        var isSecGeneratedLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                        if ((isUsecGeneratedLayer || isSecGeneratedLayer) && IsVerticalLike(a, b))
                        {
                            generatedUsecVerticalTargets.Add((a, b));
                        }

                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add((id, a, b));
                        }

                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    sourceSegments.Add((id, a, b, isUsecLayer));
                    if (isUsecLayer && IsVerticalLike(a, b))
                    {
                        verticalUsecBoundaries.Add((a, b));
                    }
                }

                if (sourceSegments.Count == 0 || verticalUsecBoundaries.Count == 0 || generatedUsecVerticalTargets.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double touchTol = 0.20;
                const double cornerTol = 0.10;
                const double minExtend = 0.05;
                const double maxExtend = 40.0;
                const double endpointMoveTol = 0.05;
                var adjusted = 0;
                var lsdMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var movedSourceSegments = new List<(ObjectId Id, Point2d OldA, Point2d OldB, Point2d NewA, Point2d NewB)>();

                foreach (var src in sourceSegments)
                {
                    // Never shift original L-USEC south-boundary segments to generated 20.12 targets.
                    if (src.IsUsec)
                    {
                        continue;
                    }

                    if (!IsHorizontalLike(src.A, src.B))
                    {
                        continue;
                    }

                    var west = src.A;
                    var east = src.B;
                    if (east.X < west.X)
                    {
                        var tmp = west;
                        west = east;
                        east = tmp;
                    }

                    // Must be west-half style segment: east endpoint has horizontal continuation to the east.
                    var hasEastContinuation = false;
                    foreach (var other in sourceSegments)
                    {
                        if (other.Id == src.Id || !IsHorizontalLike(other.A, other.B))
                        {
                            continue;
                        }

                        if (Near(east, other.A, cornerTol) && other.B.X > (east.X + cornerTol))
                        {
                            hasEastContinuation = true;
                            break;
                        }

                        if (Near(east, other.B, cornerTol) && other.A.X > (east.X + cornerTol))
                        {
                            hasEastContinuation = true;
                            break;
                        }
                    }

                    if (!hasEastContinuation)
                    {
                        continue;
                    }

                    // Must touch west vertical L-USEC and represent SW corner behavior
                    // (connected vertical line goes north from this corner).
                    var touchesWestUsec = false;
                    var hasNorthwardVertical = false;
                    foreach (var boundary in verticalUsecBoundaries)
                    {
                        var d = DistancePointToSegment(west, boundary.A, boundary.B);
                        if (d > touchTol)
                        {
                            continue;
                        }

                        touchesWestUsec = true;
                        var da = west.GetDistanceTo(boundary.A);
                        var db = west.GetDistanceTo(boundary.B);
                        var far = da >= db ? boundary.A : boundary.B;
                        if (far.Y > (west.Y + cornerTol))
                        {
                            hasNorthwardVertical = true;
                        }
                    }

                    if (!touchesWestUsec || !hasNorthwardVertical)
                    {
                        continue;
                    }

                    var outward = west - east;
                    var outwardLen = outward.Length;
                    if (outwardLen <= 1e-6)
                    {
                        continue;
                    }

                    var outwardDir = outward / outwardLen;
                    double? bestT = null;
                    foreach (var target in generatedUsecVerticalTargets)
                    {
                        if (!TryIntersectInfiniteLineWithSegment(west, outwardDir, target.A, target.B, out var t))
                        {
                            continue;
                        }

                        if (t <= minExtend || t > maxExtend)
                        {
                            continue;
                        }

                        if (!bestT.HasValue || t < bestT.Value)
                        {
                            bestT = t;
                        }
                    }

                    var finalWest = west;
                    if (bestT.HasValue)
                    {
                        finalWest = west + (outwardDir * bestT.Value);
                    }
                    else
                    {
                        var westAtGeneratedTarget = false;
                        for (var gi = 0; gi < generatedUsecVerticalTargets.Count; gi++)
                        {
                            var target = generatedUsecVerticalTargets[gi];
                            if (DistancePointToSegment(west, target.A, target.B) <= touchTol)
                            {
                                westAtGeneratedTarget = true;
                                break;
                            }
                        }

                        if (!westAtGeneratedTarget)
                        {
                            continue;
                        }
                    }

                    var eastAnchor = east;

                    if (finalWest.GetDistanceTo(west) <= endpointMoveTol)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(src.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (writable is Line ln)
                    {
                        if (west.GetDistanceTo(src.A) <= west.GetDistanceTo(src.B))
                        {
                            ln.StartPoint = new Point3d(finalWest.X, finalWest.Y, ln.StartPoint.Z);
                        }
                        else
                        {
                            ln.EndPoint = new Point3d(finalWest.X, finalWest.Y, ln.EndPoint.Z);
                        }
                    }
                    else if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        if (west.GetDistanceTo(src.A) <= west.GetDistanceTo(src.B))
                        {
                            pl.SetPointAt(0, finalWest);
                        }
                        else
                        {
                            pl.SetPointAt(1, finalWest);
                        }
                    }
                    else
                    {
                        continue;
                    }

                    adjusted++;
                    lsdMidpointAdjustments.Add((west, eastAnchor, Midpoint(west, eastAnchor), Midpoint(finalWest, eastAnchor)));
                    movedSourceSegments.Add((src.Id, west, eastAnchor, finalWest, eastAnchor));
                }

                var blindSiblingErased = 0;
                if (movedSourceSegments.Count > 0)
                {
                    const double siblingEndpointTol = 0.35;
                    var movedIds = new HashSet<ObjectId>(movedSourceSegments.Select(m => m.Id));
                    foreach (var source in sourceSegments)
                    {
                        if (movedIds.Contains(source.Id))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity sibling) || sibling.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(sibling, out var aSibling, out var bSibling) || !IsHorizontalLike(aSibling, bSibling))
                        {
                            continue;
                        }

                        var erase = false;
                        for (var mi = 0; mi < movedSourceSegments.Count; mi++)
                        {
                            var moved = movedSourceSegments[mi];
                            if (!AreSegmentsDuplicateOrCollinearOverlap(aSibling, bSibling, moved.OldA, moved.OldB))
                            {
                                continue;
                            }

                            if (!AreSegmentEndpointsNear(aSibling, bSibling, moved.OldA, moved.OldB, siblingEndpointTol))
                            {
                                continue;
                            }

                            if (AreSegmentEndpointsNear(aSibling, bSibling, moved.NewA, moved.NewB, siblingEndpointTol))
                            {
                                continue;
                            }

                            erase = true;
                            break;
                        }

                        if (!erase)
                        {
                            continue;
                        }

                        sibling.Erase();
                        blindSiblingErased++;
                    }
                }

                var lsdAdjusted = 0;
                if (lsdMidpointAdjustments.Count > 0 && lsdSegments.Count > 0)
                {
                    const double lsdOldSegmentTol = 0.35;
                    const double lsdOldMidpointTol = 8.5;
                    const double lsdMaxMove = 80.0;

                    bool TryBestMidpoint(Point2d endpoint, out Point2d midpoint, out double bestDistance, out double moveDistance)
                    {
                        midpoint = endpoint;
                        bestDistance = double.MaxValue;
                        moveDistance = double.MaxValue;
                        var bestSegDistance = double.MaxValue;

                        for (var ei = 0; ei < lsdMidpointAdjustments.Count; ei++)
                        {
                            var adj = lsdMidpointAdjustments[ei];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(adj.OldMid);
                            if (d > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var isBetterSegment = segDistance < (bestSegDistance - 1e-6);
                            var isTiedSegment = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var isBetterMidDistance = isTiedSegment && d < (bestDistance - 1e-6);
                            var isTiedMidDistance = isTiedSegment && Math.Abs(d - bestDistance) <= 1e-6;
                            var isBetterMoveOnTie = isTiedMidDistance && move < moveDistance;
                            if (!isBetterSegment && !isBetterMidDistance && !isBetterMoveOnTie)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestDistance = d;
                            moveDistance = move;
                            midpoint = adj.NewMid;
                        }

                        return bestDistance < double.MaxValue;
                    }

                    foreach (var lsd in lsdSegments)
                    {
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryBestMidpoint(p0, out var mid0, out var d0, out _);
                        var has1 = TryBestMidpoint(p1, out var mid1, out var d1, out _);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var moveStart = has0;
                        var targetMid = mid0;
                        if (!has0 || (has1 && d1 < d0))
                        {
                            moveStart = false;
                            targetMid = mid1;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            lsdPoly.SetPointAt(moveStart ? 0 : 1, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: extended {adjusted} SW south-boundary west endpoint(s) to next L-USEC line.");
                }
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of SW south-boundary extension line(s) [segment-anchored].");
                }
                if (blindSiblingErased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {blindSiblingErased} blind-line sibling segment(s) after SW extension.");
                }
            }
        }
                private static void ExtendNwQuarterWestUsecNorthToNextHorizontalUsec(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
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

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var horizontalSources = new List<(ObjectId Id, Point2d A, Point2d B, bool Generated)>();
                var generatedVerticalUsec = new List<(ObjectId Id, Point2d North, Point2d South)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();

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

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add((id, a, b));
                        }

                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    var generated = generatedSet.Contains(id);
                    if (IsHorizontalLike(a, b))
                    {
                        horizontalSources.Add((id, a, b, generated));
                    }

                    if ((isUsec || isSec) && generated && IsVerticalLike(a, b))
                    {
                        var north = a;
                        var south = b;
                        if (south.Y > north.Y)
                        {
                            var tmp = north;
                            north = south;
                            south = tmp;
                        }

                        generatedVerticalUsec.Add((id, north, south));
                    }
                }

                if (horizontalSources.Count == 0 || generatedVerticalUsec.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double searchRadius = 12.0;
                const double maxExtend = 40.0;

                bool TryMoveEndpoint(Entity writable, Point2d oldEndpoint, Point2d newEndpoint)
                {
                    if (newEndpoint.GetDistanceTo(oldEndpoint) <= endpointMoveTol)
                    {
                        return false;
                    }

                    if (writable is Line ln)
                    {
                        var start = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        var end = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        if (start.GetDistanceTo(oldEndpoint) <= end.GetDistanceTo(oldEndpoint))
                        {
                            ln.StartPoint = new Point3d(newEndpoint.X, newEndpoint.Y, ln.StartPoint.Z);
                        }
                        else
                        {
                            ln.EndPoint = new Point3d(newEndpoint.X, newEndpoint.Y, ln.EndPoint.Z);
                        }

                        return true;
                    }

                    if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)
                    {
                        var p0 = pl.GetPoint2dAt(0);
                        var p1 = pl.GetPoint2dAt(1);
                        if (p0.GetDistanceTo(oldEndpoint) <= p1.GetDistanceTo(oldEndpoint))
                        {
                            pl.SetPointAt(0, newEndpoint);
                        }
                        else
                        {
                            pl.SetPointAt(1, newEndpoint);
                        }

                        return true;
                    }

                    return false;
                }

                var adjusted = 0;
                var usedHorizontals = new HashSet<ObjectId>();
                var pairTried = 0;
                var pairChosen = 0;
                var lsdMidpointAdjustments = new List<(Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var movedHorizontalSegments = new List<(ObjectId Id, Point2d OldA, Point2d OldB, Point2d NewA, Point2d NewB)>();

                for (var pass = 0; pass < 2; pass++)
                {
                    var allowGenerated = pass == 1;
                    var adjustedThisPass = 0;

                    foreach (var anchor in generatedVerticalUsec)
                    {
                        var bestId = ObjectId.Null;
                        var bestOld = default(Point2d);
                        var bestOther = default(Point2d);
                        var bestNew = default(Point2d);
                        var bestScore = double.MaxValue;

                        for (var i = 0; i < horizontalSources.Count; i++)
                        {
                            var src = horizontalSources[i];
                            if (!allowGenerated && src.Generated)
                            {
                                continue;
                            }

                            if (usedHorizontals.Contains(src.Id))
                            {
                                continue;
                            }

                            var west = src.A;
                            var east = src.B;
                            if (east.X < west.X)
                            {
                                var tmp = west;
                                west = east;
                                east = tmp;
                            }

                            var midpointTarget = Midpoint(anchor.North, anchor.South);

                            var dx = src.B.X - src.A.X;
                            if (Math.Abs(dx) <= 1e-8)
                            {
                                continue;
                            }

                            var t = (anchor.North.X - src.A.X) / dx;
                            var yTarget = src.A.Y + (t * (src.B.Y - src.A.Y));
                            var candidateTarget = new Point2d(anchor.North.X, yTarget);

                            var moveDist = west.GetDistanceTo(candidateTarget);
                            if (moveDist <= endpointMoveTol || moveDist > maxExtend)
                            {
                                continue;
                            }

                            var anchorDist = west.GetDistanceTo(anchor.North);
                            if (anchorDist > searchRadius)
                            {
                                continue;
                            }

                            var westDirVec = west - east;
                            var westLen = westDirVec.Length;
                            if (westLen <= 1e-6)
                            {
                                continue;
                            }

                            var westDir = westDirVec / westLen;
                            if ((candidateTarget - west).DotProduct(westDir) <= endpointMoveTol)
                            {
                                continue;
                            }

                            const double midpointSnapTol = 0.10;
                            var finalTarget = candidateTarget;
                            if (candidateTarget.GetDistanceTo(midpointTarget) <= midpointSnapTol)
                            {
                                finalTarget = midpointTarget;
                            }

                            var score = moveDist + (2.0 * Math.Abs(west.Y - anchor.North.Y));
                            pairTried++;
                            if (score < bestScore)
                            {
                                bestScore = score;
                                bestId = src.Id;
                                bestOld = west;
                                bestOther = east;
                                bestNew = finalTarget;
                            }
                        }

                        if (bestId.IsNull)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(bestId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (TryMoveEndpoint(writable, bestOld, bestNew))
                        {
                            adjusted++;
                            adjustedThisPass++;
                            pairChosen++;
                            usedHorizontals.Add(bestId);
                            lsdMidpointAdjustments.Add((
                                bestOld,
                                bestOther,
                                Midpoint(bestOld, bestOther),
                                Midpoint(bestNew, bestOther)));
                            movedHorizontalSegments.Add((bestId, bestOld, bestOther, bestNew, bestOther));
                        }
                    }

                    if (adjustedThisPass > 0)
                    {
                        break;
                    }
                }

                var blindSiblingErased = 0;
                if (movedHorizontalSegments.Count > 0)
                {
                    const double siblingEndpointTol = 0.35;
                    var movedIds = new HashSet<ObjectId>(movedHorizontalSegments.Select(m => m.Id));
                    for (var i = 0; i < horizontalSources.Count; i++)
                    {
                        var source = horizontalSources[i];
                        if (movedIds.Contains(source.Id))
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity sibling) || sibling.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(sibling, out var aSibling, out var bSibling) || !IsHorizontalLike(aSibling, bSibling))
                        {
                            continue;
                        }

                        var erase = false;
                        for (var mi = 0; mi < movedHorizontalSegments.Count; mi++)
                        {
                            var moved = movedHorizontalSegments[mi];
                            if (!AreSegmentsDuplicateOrCollinearOverlap(aSibling, bSibling, moved.OldA, moved.OldB))
                            {
                                continue;
                            }

                            if (!AreSegmentEndpointsNear(aSibling, bSibling, moved.OldA, moved.OldB, siblingEndpointTol))
                            {
                                continue;
                            }

                            if (AreSegmentEndpointsNear(aSibling, bSibling, moved.NewA, moved.NewB, siblingEndpointTol))
                            {
                                continue;
                            }

                            erase = true;
                            break;
                        }

                        if (!erase)
                        {
                            continue;
                        }

                        sibling.Erase();
                        blindSiblingErased++;
                    }
                }

                var lsdAdjusted = 0;
                if (lsdMidpointAdjustments.Count > 0 && lsdSegments.Count > 0)
                {
                    const double lsdOldSegmentTol = 0.35;
                    const double lsdOldMidpointTol = 12.0;
                    const double lsdMaxMove = 40.0;

                    bool TryBestMidpoint(Point2d endpoint, out Point2d midpoint, out double bestDistance)
                    {
                        midpoint = endpoint;
                        bestDistance = double.MaxValue;
                        var bestSegDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;

                        for (var i = 0; i < lsdMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdMidpointAdjustments[i];
                            var segDistance = DistancePointToSegment(endpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var d = endpoint.GetDistanceTo(adj.OldMid);
                            if (d > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var move = endpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && d < (bestDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(d - bestDistance) <= 1e-6;
                            var betterMove = tiedMid && move < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            bestSegDistance = segDistance;
                            bestDistance = d;
                            bestMoveDistance = move;
                            midpoint = adj.NewMid;
                        }

                        return bestDistance < double.MaxValue;
                    }

                    for (var i = 0; i < lsdSegments.Count; i++)
                    {
                        var lsd = lsdSegments[i];
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        var has0 = TryBestMidpoint(p0, out var mid0, out var d0);
                        var has1 = TryBestMidpoint(p1, out var mid1, out var d1);
                        if (!has0 && !has1)
                        {
                            continue;
                        }

                        var moveStart = has0;
                        var targetMid = mid0;
                        if (!has0 || (has1 && d1 < d0))
                        {
                            moveStart = false;
                            targetMid = mid1;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            lsdPoly.SetPointAt(moveStart ? 0 : 1, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: connected {adjusted} NW RA corner gap endpoint(s) (H={adjusted}, V=0).");
                if (lsdAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of NW west-end extension line(s).");
                }
                if (blindSiblingErased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {blindSiblingErased} blind-line sibling segment(s) after NW extension.");
                }
                if (adjusted == 0)
                {
                    logger?.WriteLine($"Cleanup: NW simple candidates (H={horizontalSources.Count}, GV={generatedVerticalUsec.Count}, tries={pairTried}, chosen={pairChosen}).");
                }
            }
        }

        private static void ConnectUsecBlindSouthwestTwentyTwelveLines(
            Database database,
            IEnumerable<QuarterLabelInfo> labelQuarterInfos,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || labelQuarterInfos == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var targetInfos = labelQuarterInfos
                .Where(info =>
                    info != null &&
                    IsUsecSouthExtensionSection(info.SectionKey.Section))
                .ToList();
            if (targetInfos.Count == 0)
            {
                logger?.WriteLine("Cleanup: SW L-USEC 20.12 blind-line connect skipped (no target section info).");
                return;
            }

            var targetSectionIds = targetInfos
                .Where(info => !info.SectionPolylineId.IsNull)
                .Select(info => info.SectionPolylineId)
                .Distinct()
                .ToList();
            var sectionTargets = new List<(ObjectId SectionId, Point2d SwCorner, Vector2d EastUnit, Vector2d NorthUnit, double Width, double Height, Extents3d Window)>();
            var clipWindows = new List<Extents3d>();
            if (targetSectionIds.Count > 0)
            {
                const double swWindowBuffer = 120.0;
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    foreach (var sectionId in targetSectionIds)
                    {
                        if (!(tr.GetObject(sectionId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            var ext = section.GeometricExtents;
                            if (!TryGetQuarterAnchors(section, out var sectionAnchors))
                            {
                                sectionAnchors = GetFallbackAnchors(section);
                            }

                            var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
                            var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));
                            var sectionWidth = sectionAnchors.Left.GetDistanceTo(sectionAnchors.Right);
                            var sectionHeight = sectionAnchors.Bottom.GetDistanceTo(sectionAnchors.Top);
                            if (sectionWidth <= 1e-6)
                            {
                                sectionWidth = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X);
                            }

                            if (sectionHeight <= 1e-6)
                            {
                                sectionHeight = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y);
                            }

                            Point2d swCorner;
                            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out swCorner))
                            {
                                swCorner = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                            }

                            var midX = 0.5 * (ext.MinPoint.X + ext.MaxPoint.X);
                            var midY = 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y);
                            var swWindow = new Extents3d(
                                new Point3d(ext.MinPoint.X - swWindowBuffer, ext.MinPoint.Y - swWindowBuffer, 0.0),
                                new Point3d(midX + swWindowBuffer, midY + swWindowBuffer, 0.0));
                            clipWindows.Add(swWindow);
                            sectionTargets.Add((sectionId, swCorner, eastUnit, northUnit, sectionWidth, sectionHeight, swWindow));
                        }
                        catch
                        {
                        }
                    }

                    tr.Commit();
                }
            }

            if (clipWindows.Count == 0 || sectionTargets.Count == 0)
            {
                logger?.WriteLine("Cleanup: SW L-USEC 20.12 blind-line connect skipped (no clip windows).");
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

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                bool IsPointInWindow(Point2d p, Extents3d window)
                {
                    return p.X >= window.MinPoint.X && p.X <= window.MaxPoint.X &&
                           p.Y >= window.MinPoint.Y && p.Y <= window.MaxPoint.Y;
                }

                bool DoesSegmentIntersectWindow(Point2d a, Point2d b, Extents3d window)
                {
                    return IsPointInWindow(a, window) ||
                           IsPointInWindow(b, window) ||
                           TryClipSegmentToWindow(a, b, window, out _, out _);
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

                // Ownership note for 30.18/20.12 logic:
                // - west-side road allowances belong to the section on their west (left)
                // - south-side road allowances belong to the section above (north)
                // so this pass gathers nearby sec/usec segments from both generated and existing geometry.
                var roadAllowanceSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Generated)>();
                var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
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

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add((id, a, b));
                        }

                        continue;
                    }

                    var isSecLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    if (!isSecLayer && !isUsecLayer)
                    {
                        continue;
                    }

                    roadAllowanceSegments.Add((id, a, b, generatedSet.Contains(id)));
                }

                if (roadAllowanceSegments.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine("Cleanup: SW L-USEC 20.12 blind-line connect skipped (no generated RA segments).");
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double primaryExpectedOffset = 20.12;
                const double secondaryExpectedOffset = 30.18;
                const double offsetTol = 3.0;
                const double maxEndpointGap = 45.0;
                var usedHorizontalSegments = new HashSet<ObjectId>();
                var connectedPairs = 0;
                var movedHorizontalEndpoints = 0;
                var movedVerticalEndpoints = 0;
                var sectionsWithCandidates = 0;
                var pairCandidatesEvaluated = 0;
                var forcedCornerAttempts = 0;
                var forcedCornerConnected = 0;
                var lsdMidpointAdjustments = new List<(ObjectId SectionId, Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var lsdVerticalMidpointAdjustments = new List<(ObjectId SectionId, Point2d OldA, Point2d OldB, Point2d OldMid, Point2d NewMid)>();
                var lsdSouthTargetMidpoints = new List<(ObjectId SectionId, Point2d Midpoint, Point2d SwCorner, Vector2d EastUnit, Vector2d NorthUnit)>();
                var lsdFallbackEndpointAdjustments = 0;

                for (var si = 0; si < sectionTargets.Count; si++)
                {
                    var sectionTarget = sectionTargets[si];
                    var swCorner = sectionTarget.SwCorner;
                    var eastUnit = sectionTarget.EastUnit;
                    var northUnit = sectionTarget.NorthUnit;

                    double ToU(Point2d p) => (p - swCorner).DotProduct(eastUnit);
                    double ToV(Point2d p) => (p - swCorner).DotProduct(northUnit);

                    (
                        List<(ObjectId Id, bool IsGenerated, Point2d WestPoint, Point2d EastPoint, bool WestIsStart, double VLine, double WestU, double WestV)> Horizontals,
                        List<(ObjectId Id, bool IsGenerated, Point2d SouthPoint, Point2d NorthPoint, bool SouthIsStart, double ULine, double SouthU, double SouthV)> Verticals
                    )
                    CollectCandidatesForOffset(double expectedOffsetForBand)
                    {
                        var horizontalsForOffset = new List<(ObjectId Id, bool IsGenerated, Point2d WestPoint, Point2d EastPoint, bool WestIsStart, double VLine, double WestU, double WestV)>();
                        var verticalsForOffset = new List<(ObjectId Id, bool IsGenerated, Point2d SouthPoint, Point2d NorthPoint, bool SouthIsStart, double ULine, double SouthU, double SouthV)>();
                        for (var gi = 0; gi < roadAllowanceSegments.Count; gi++)
                        {
                            var seg = roadAllowanceSegments[gi];
                            if (usedHorizontalSegments.Contains(seg.Id))
                            {
                                continue;
                            }

                            if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                            {
                                continue;
                            }

                            var d = seg.B - seg.A;
                            var len = d.Length;
                            if (len <= 1e-6)
                            {
                                continue;
                            }

                            var uA = ToU(seg.A);
                            var vA = ToV(seg.A);
                            var uB = ToU(seg.B);
                            var vB = ToV(seg.B);
                            var eastComp = Math.Abs(d.DotProduct(eastUnit));
                            var northComp = Math.Abs(d.DotProduct(northUnit));
                            if (eastComp >= northComp)
                            {
                                var westIsStart = uA <= uB;
                                var westPoint = westIsStart ? seg.A : seg.B;
                                var eastPoint = westIsStart ? seg.B : seg.A;
                                var westU = westIsStart ? uA : uB;
                                var westV = westIsStart ? vA : vB;
                                var vLine = 0.5 * (vA + vB);
                                if (Math.Abs(Math.Abs(vLine) - expectedOffsetForBand) > offsetTol)
                                {
                                    continue;
                                }

                                horizontalsForOffset.Add((seg.Id, seg.Generated, westPoint, eastPoint, westIsStart, vLine, westU, westV));
                            }
                            else
                            {
                                var southIsStart = vA <= vB;
                                var southPoint = southIsStart ? seg.A : seg.B;
                                var northPoint = southIsStart ? seg.B : seg.A;
                                var southU = southIsStart ? uA : uB;
                                var southV = southIsStart ? vA : vB;
                                var uLine = 0.5 * (uA + uB);
                                if (Math.Abs(Math.Abs(uLine) - expectedOffsetForBand) > offsetTol)
                                {
                                    continue;
                                }

                                verticalsForOffset.Add((seg.Id, seg.Generated, southPoint, northPoint, southIsStart, uLine, southU, southV));
                            }
                        }

                        return (horizontalsForOffset, verticalsForOffset);
                    }

                    var activeExpectedOffset = primaryExpectedOffset;
                    var primaryCandidates = CollectCandidatesForOffset(primaryExpectedOffset);
                    var horizontals = primaryCandidates.Horizontals;
                    var verticals = primaryCandidates.Verticals;

                    if (horizontals.Count == 0)
                    {
                        continue;
                    }

                    var bestSouthHorizontal = horizontals
                        .OrderBy(h => Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset))
                        .ThenByDescending(h => h.WestPoint.GetDistanceTo(h.EastPoint))
                        .First();
                    lsdSouthTargetMidpoints.Add((
                        sectionTarget.SectionId,
                        Midpoint(bestSouthHorizontal.WestPoint, bestSouthHorizontal.EastPoint),
                        sectionTarget.SwCorner,
                        sectionTarget.EastUnit,
                        sectionTarget.NorthUnit));

                    if (verticals.Count == 0)
                    {
                        // Sparse/single-section builds can miss the vertical SW corner leg.
                        // Keep LSD south-end adjustment alive by synthesizing the source (old) segment
                        // one 10.06m band north of the generated 20.12 horizontal.
                        sectionsWithCandidates++;
                        var oldWest = bestSouthHorizontal.WestPoint + (northUnit * activeExpectedOffset);
                        var oldEast = bestSouthHorizontal.EastPoint + (northUnit * activeExpectedOffset);
                        lsdMidpointAdjustments.Add((
                            sectionTarget.SectionId,
                            oldWest,
                            oldEast,
                            Midpoint(oldWest, oldEast),
                            Midpoint(bestSouthHorizontal.WestPoint, bestSouthHorizontal.EastPoint)));
                        continue;
                    }

                    sectionsWithCandidates++;
                    const double swCornerBand = 220.0;
                    var forcedHorizontalPool = horizontals
                        .Where(h => Math.Min(
                            h.WestPoint.GetDistanceTo(swCorner),
                            h.EastPoint.GetDistanceTo(swCorner)) <= swCornerBand)
                        .ToList();
                    if (forcedHorizontalPool.Count == 0)
                    {
                        forcedHorizontalPool = horizontals;
                    }

                    var forcedVerticalPool = verticals
                        .Where(v => Math.Min(
                            v.SouthPoint.GetDistanceTo(swCorner),
                            v.NorthPoint.GetDistanceTo(swCorner)) <= swCornerBand)
                        .ToList();
                    if (forcedVerticalPool.Count == 0)
                    {
                        forcedVerticalPool = verticals;
                    }

                    var forcedHorizontal = forcedHorizontalPool
                        .OrderBy(h => Math.Min(
                            h.WestPoint.GetDistanceTo(swCorner),
                            h.EastPoint.GetDistanceTo(swCorner)))
                        .ThenByDescending(h => h.IsGenerated)
                        .ThenBy(h => Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset))
                        .ThenBy(h => h.WestPoint.GetDistanceTo(h.EastPoint))
                        .First();
                    var forcedVertical = forcedVerticalPool
                        .OrderBy(v => Math.Min(
                            v.SouthPoint.GetDistanceTo(swCorner),
                            v.NorthPoint.GetDistanceTo(swCorner)))
                        .ThenByDescending(v => v.IsGenerated)
                        .ThenBy(v => Math.Abs(Math.Abs(v.ULine) - activeExpectedOffset))
                        .ThenBy(v => v.SouthPoint.GetDistanceTo(v.NorthPoint))
                        .First();
                    var forcedTarget = swCorner + (eastUnit * forcedVertical.ULine) + (northUnit * forcedHorizontal.VLine);
                    if (TryIntersectInfiniteLines(
                        forcedHorizontal.WestPoint,
                        forcedHorizontal.EastPoint,
                        forcedVertical.SouthPoint,
                        forcedVertical.NorthPoint,
                        out var forcedIntersection))
                    {
                        forcedTarget = forcedIntersection;
                    }

                    if (IsPointInWindow(forcedTarget, sectionTarget.Window) ||
                        forcedHorizontal.WestPoint.GetDistanceTo(forcedTarget) <= maxEndpointGap ||
                        forcedVertical.SouthPoint.GetDistanceTo(forcedTarget) <= maxEndpointGap)
                    {
                        forcedCornerAttempts++;
                        var forceHMove = forcedHorizontal.WestPoint.GetDistanceTo(forcedTarget);
                        var forceVMove = forcedVertical.SouthPoint.GetDistanceTo(forcedTarget);
                        var allowForceH = forceHMove <= maxEndpointGap;
                        var allowForceV = forceVMove <= maxEndpointGap;
                            if (allowForceH || allowForceV)
                            {
                                Entity? forcedHWritable = null;
                                var forceMoveHStart = forcedHorizontal.WestIsStart;
                                if (forcedHorizontal.EastPoint.GetDistanceTo(forcedTarget) < forcedHorizontal.WestPoint.GetDistanceTo(forcedTarget))
                                {
                                    forceMoveHStart = !forceMoveHStart;
                                }
                                if (allowForceH)
                                {
                                    forcedHWritable = tr.GetObject(forcedHorizontal.Id, OpenMode.ForWrite, false) as Entity;
                                    if (forcedHWritable == null || forcedHWritable.IsErased)
                                    {
                                        allowForceH = false;
                                    }
                                }

                                Entity? forcedVWritable = null;
                                var forceMoveVStart = forcedVertical.SouthIsStart;
                                if (forcedVertical.NorthPoint.GetDistanceTo(forcedTarget) < forcedVertical.SouthPoint.GetDistanceTo(forcedTarget))
                                {
                                    forceMoveVStart = !forceMoveVStart;
                                }
                                if (allowForceV)
                                {
                                    forcedVWritable = tr.GetObject(forcedVertical.Id, OpenMode.ForWrite, false) as Entity;
                                    if (forcedVWritable == null || forcedVWritable.IsErased)
                                    {
                                        allowForceV = false;
                                    }
                                }

                                var movedForcedV = allowForceV &&
                                                   forcedVWritable != null &&
                                               TryMoveEndpoint(forcedVWritable, forceMoveVStart, forcedTarget, endpointMoveTol);
                                var movedForcedH = allowForceH &&
                                                   forcedHWritable != null &&
                                               TryMoveEndpoint(forcedHWritable, forceMoveHStart, forcedTarget, endpointMoveTol);
                                if (movedForcedH || movedForcedV)
                                {
                                    forcedCornerConnected++;
                                    connectedPairs++;
                                    if (movedForcedH)
                                    {
                                        var fixedHPoint = forceMoveHStart ? forcedHorizontal.EastPoint : forcedHorizontal.WestPoint;
                                        usedHorizontalSegments.Add(forcedHorizontal.Id);
                                        movedHorizontalEndpoints++;
                                        lsdMidpointAdjustments.Add((
                                            sectionTarget.SectionId,
                                            forcedHorizontal.WestPoint,
                                            forcedHorizontal.EastPoint,
                                            Midpoint(forcedHorizontal.WestPoint, forcedHorizontal.EastPoint),
                                            Midpoint(forcedTarget, fixedHPoint)));
                                    }

                                    if (movedForcedV)
                                    {
                                        var fixedVPoint = forceMoveVStart ? forcedVertical.NorthPoint : forcedVertical.SouthPoint;
                                        movedVerticalEndpoints++;
                                        lsdVerticalMidpointAdjustments.Add((
                                            sectionTarget.SectionId,
                                            forcedVertical.SouthPoint,
                                            forcedVertical.NorthPoint,
                                            Midpoint(forcedVertical.SouthPoint, forcedVertical.NorthPoint),
                                            Midpoint(forcedTarget, fixedVPoint)));
                                    }

                                // Deterministic SW 20.12 L-corner handled for this section.
                                continue;
                            }
                        }
                    }

                    var pairCandidates = new List<(
                        ObjectId HId,
                        Point2d HOldWest,
                        Point2d HOldEast,
                        bool HMoveStart,
                        double HMove,
                        ObjectId VId,
                        Point2d VOldSouth,
                        Point2d VOldNorth,
                        bool VMoveStart,
                        double VMove,
                        bool MoveHorizontal,
                        bool MoveVertical,
                        Point2d Target,
                        double Score)>();
                    for (var hi = 0; hi < horizontals.Count; hi++)
                    {
                        var h = horizontals[hi];
                        for (var vi = 0; vi < verticals.Count; vi++)
                        {
                            var v = verticals[vi];
                            if (h.Id == v.Id)
                            {
                                continue;
                            }

                            var target = swCorner + (eastUnit * v.ULine) + (northUnit * h.VLine);
                            if (!IsPointInWindow(target, sectionTarget.Window))
                            {
                                continue;
                            }

                            var hMove = h.WestPoint.GetDistanceTo(target);
                            if (hMove > maxEndpointGap)
                            {
                                continue;
                            }

                            // Accept long vertical 20.12 lines by evaluating against the vertical segment,
                            // not the south endpoint distance.
                            var vDistance = DistancePointToSegment(target, v.SouthPoint, v.NorthPoint);
                            if (vDistance > 2.0)
                            {
                                continue;
                            }

                            var vMove = v.SouthPoint.GetDistanceTo(target);
                            var canMoveHorizontal = hMove <= maxEndpointGap;
                            var canMoveVertical = vMove <= maxEndpointGap;
                            if (!canMoveHorizontal && !canMoveVertical)
                            {
                                continue;
                            }

                            // Deterministic SW 20.12 L-corner rule: when possible, move both endpoints
                            // to the computed 20.12 intersection so the two lines actually join.
                            var moveHorizontal = canMoveHorizontal;
                            var moveVertical = canMoveVertical;
                            var chosenMove = (moveHorizontal ? hMove : 0.0) + (moveVertical ? vMove : 0.0);
                            var cornerGap = h.WestPoint.GetDistanceTo(v.SouthPoint);

                            var score =
                                cornerGap +
                                chosenMove +
                                vDistance +
                                Math.Abs(Math.Abs(v.ULine) - activeExpectedOffset) +
                                Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset);
                            pairCandidates.Add((
                                h.Id,
                                h.WestPoint,
                                h.EastPoint,
                                h.WestIsStart,
                                hMove,
                                v.Id,
                                v.SouthPoint,
                                v.NorthPoint,
                                v.SouthIsStart,
                                vMove,
                                moveHorizontal,
                                moveVertical,
                                target,
                                score));
                        }
                    }

                    pairCandidatesEvaluated += pairCandidates.Count;
                    if (pairCandidates.Count == 0)
                    {
                        var bestFallbackHorizontal = horizontals
                            .OrderBy(h => Math.Abs(Math.Abs(h.VLine) - activeExpectedOffset))
                            .ThenByDescending(h => h.WestPoint.GetDistanceTo(h.EastPoint))
                            .First();
                        var oldWest = bestFallbackHorizontal.WestPoint + (northUnit * activeExpectedOffset);
                        var oldEast = bestFallbackHorizontal.EastPoint + (northUnit * activeExpectedOffset);
                        lsdMidpointAdjustments.Add((
                            sectionTarget.SectionId,
                            oldWest,
                            oldEast,
                            Midpoint(oldWest, oldEast),
                            Midpoint(bestFallbackHorizontal.WestPoint, bestFallbackHorizontal.EastPoint)));
                        continue;
                    }

                    var orderedCandidates = pairCandidates
                        .OrderBy(c => c.Score)
                        .ToList();
                    for (var ci = 0; ci < orderedCandidates.Count; ci++)
                    {
                        var candidate = orderedCandidates[ci];
                        if (usedHorizontalSegments.Contains(candidate.HId))
                        {
                            continue;
                        }

                        Entity? hWritable = null;
                        if (candidate.MoveHorizontal)
                        {
                            hWritable = tr.GetObject(candidate.HId, OpenMode.ForWrite, false) as Entity;
                            if (hWritable == null || hWritable.IsErased)
                            {
                                continue;
                            }
                        }

                        Entity? vWritable = null;
                        if (candidate.MoveVertical)
                        {
                            vWritable = tr.GetObject(candidate.VId, OpenMode.ForWrite, false) as Entity;
                            if (vWritable == null || vWritable.IsErased)
                            {
                                continue;
                            }
                        }

                        var movedV = candidate.MoveVertical &&
                                     vWritable != null &&
                                     TryMoveEndpoint(vWritable, candidate.VMoveStart, candidate.Target, endpointMoveTol);
                        var movedH = candidate.MoveHorizontal &&
                                     hWritable != null &&
                                     TryMoveEndpoint(hWritable, candidate.HMoveStart, candidate.Target, endpointMoveTol);
                        if (!movedH && !movedV)
                        {
                            continue;
                        }

                        if (movedH)
                        {
                            usedHorizontalSegments.Add(candidate.HId);
                        }
                        if (movedH || movedV)
                        {
                            connectedPairs++;
                        }
                        if (movedH)
                        {
                            movedHorizontalEndpoints++;
                            lsdMidpointAdjustments.Add((
                                sectionTarget.SectionId,
                                candidate.HOldWest,
                                candidate.HOldEast,
                                Midpoint(candidate.HOldWest, candidate.HOldEast),
                                Midpoint(candidate.Target, candidate.HOldEast)));
                        }

                        if (movedV)
                        {
                            movedVerticalEndpoints++;
                            lsdVerticalMidpointAdjustments.Add((
                                sectionTarget.SectionId,
                                candidate.VOldSouth,
                                candidate.VOldNorth,
                                Midpoint(candidate.VOldSouth, candidate.VOldNorth),
                                Midpoint(candidate.Target, candidate.VOldNorth)));
                        }

                        // One corner-connection per target section is enough.
                        break;
                    }
                }

                var lsdAdjusted = 0;
                if ((lsdMidpointAdjustments.Count > 0 || lsdVerticalMidpointAdjustments.Count > 0) && lsdSegments.Count > 0)
                {
                    const double lsdOldSegmentTol = 2.0;
                    const double lsdOldMidpointTol = 45.0;
                    const double lsdFallbackOldMidpointTol = 120.0;
                    const double lsdMaxMove = 80.0;
                    const double southwardTol = 0.50;
                    const double maxNorthwardCorrection = 12.5;
                    const double maxSouthwardDelta = 70.0;
                    const double eastwardTol = 1.0;
                    const double maxEastwardCorrection = 12.5;
                    const double maxWestwardDelta = 70.0;
                    const double maxCenterlineOffset = 35.0;
                    const double seamCenterlineOffset = 60.0;
                    const double ownershipUTol = 40.0;
                    const double ownershipVTol = 55.0;
                    const double ownershipFallbackUMargin = 80.0;
                    const double ownershipFallbackVMargin = 100.0;
                    var lsdConsidered = 0;
                    var lsdSkippedNoOwner = 0;
                    var lsdOwnerFallbackUsed = 0;
                    var lsdOwnerlessSouthFallbackUsed = 0;
                    var lsdProjectionFallbackUsed = 0;
                    var lsdProjectionAnySectionFallbackUsed = 0;
                    var lsdSkippedNoTarget = 0;
                    var lsdSkippedUnsupportedOrientation = 0;

                    bool TryGetOwningSectionIndex(Point2d a, Point2d b, out int sectionIndex)
                    {
                        sectionIndex = -1;
                        var mid = Midpoint(a, b);
                        var bestDistance = double.MaxValue;
                        for (var si = 0; si < sectionTargets.Count; si++)
                        {
                            var sectionTarget = sectionTargets[si];
                            if (!DoesSegmentIntersectWindow(a, b, sectionTarget.Window) &&
                                !IsPointInWindow(mid, sectionTarget.Window))
                            {
                                continue;
                            }

                            var midU = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            if (midU < -ownershipUTol || midU > (sectionTarget.Width + ownershipUTol))
                            {
                                continue;
                            }

                            var midV = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            if (midV < -(primaryExpectedOffset + ownershipVTol) || midV > (sectionTarget.Height + ownershipVTol))
                            {
                                continue;
                            }

                            var d = mid.GetDistanceTo(sectionTarget.SwCorner);
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                sectionIndex = si;
                            }
                        }

                        return sectionIndex >= 0;
                    }

                    bool TryGetNearestSectionIndexFallback(Point2d a, Point2d b, out int sectionIndex)
                    {
                        sectionIndex = -1;
                        var mid = Midpoint(a, b);
                        var bestDistance = double.MaxValue;
                        for (var si = 0; si < sectionTargets.Count; si++)
                        {
                            var sectionTarget = sectionTargets[si];
                            var midU = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            if (midU < -(ownershipUTol + ownershipFallbackUMargin) ||
                                midU > (sectionTarget.Width + ownershipUTol + ownershipFallbackUMargin))
                            {
                                continue;
                            }

                            var midV = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            if (midV < -(secondaryExpectedOffset + ownershipVTol + ownershipFallbackVMargin) ||
                                midV > (sectionTarget.Height + ownershipVTol + ownershipFallbackVMargin))
                            {
                                continue;
                            }

                            var d = mid.GetDistanceTo(sectionTarget.SwCorner);
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                sectionIndex = si;
                            }
                        }

                        return sectionIndex >= 0;
                    }

                    bool TrySelectSouthTargetMidpoint(
                        Point2d southEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint,
                        out bool usedFallback)
                    {
                        targetMidpoint = southEndpoint;
                        usedFallback = false;
                        var sectionTarget = sectionTargets[sectionIndex];
                        var endpointU = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                        var endpointV = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);

                        var found = false;
                        var bestSouthDelta = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < lsdMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var segDistance = DistancePointToSegment(southEndpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var oldMidDistance = southEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var targetV = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var targetOffsetError = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset);
                            if (targetOffsetError > offsetTol)
                            {
                                continue;
                            }

                            var southDelta = endpointV - targetV;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Fallback for noisy exploded LSD geometry: same section only, still southward-only.
                        for (var i = 0; i < lsdMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var oldMidDistance = southEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdFallbackOldMidpointTol)
                            {
                                continue;
                            }

                            var targetV = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var targetOffsetError = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset);
                            if (targetOffsetError > offsetTol)
                            {
                                continue;
                            }

                            var southDelta = endpointV - targetV;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        if (found)
                        {
                            return true;
                        }

                        for (var i = 0; i < lsdSouthTargetMidpoints.Count; i++)
                        {
                            var target = lsdSouthTargetMidpoints[i];
                            if (target.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var targetU = (target.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > maxCenterlineOffset)
                            {
                                continue;
                            }

                            var targetV = (target.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var targetOffsetError = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset);
                            if (targetOffsetError > offsetTol)
                            {
                                continue;
                            }

                            var southDelta = endpointV - targetV;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(target.Midpoint);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = target.Midpoint;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Township/range seam fallback for direct 20.12 midpoint targets:
                        // if section ownership resolution was slightly off, still allow nearby
                        // aligned 20.12 midpoint from neighboring section target records.
                        for (var i = 0; i < lsdSouthTargetMidpoints.Count; i++)
                        {
                            var target = lsdSouthTargetMidpoints[i];
                            var targetU = (target.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > seamCenterlineOffset)
                            {
                                continue;
                            }

                            var targetV = (target.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var targetOffsetError = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset);
                            if (targetOffsetError > offsetTol)
                            {
                                continue;
                            }

                            var southDelta = endpointV - targetV;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(target.Midpoint);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = target.Midpoint;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Township/range seam fallback: allow nearest adjusted midpoint from neighboring target section.
                        for (var i = 0; i < lsdMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdMidpointAdjustments[i];
                            var targetU = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > maxCenterlineOffset)
                            {
                                continue;
                            }

                            var targetV = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var targetOffsetError = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset);
                            if (targetOffsetError > offsetTol)
                            {
                                continue;
                            }

                            var southDelta = endpointV - targetV;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        return found;
                    }

                    bool TrySelectSouthProjectionToTwentyTwelve(
                        Point2d southEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint)
                    {
                        targetMidpoint = southEndpoint;
                        var sectionTarget = sectionTargets[sectionIndex];
                        var endpointU = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                        var endpointV = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                        const double uRangeTol = 25.0;
                        const double maxUGapToProject = 80.0;
                        const double endpointThirtyBandTol = 12.5;

                        if (Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) > endpointThirtyBandTol)
                        {
                            return false;
                        }

                        var found = false;
                        var bestUGap = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        var bestSouthDelta = double.MaxValue;
                        for (var i = 0; i < roadAllowanceSegments.Count; i++)
                        {
                            var seg = roadAllowanceSegments[i];
                            if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                            {
                                continue;
                            }

                            var d = seg.B - seg.A;
                            if (d.Length <= 1e-6)
                            {
                                continue;
                            }

                            var eastComp = Math.Abs(d.DotProduct(sectionTarget.EastUnit));
                            var northComp = Math.Abs(d.DotProduct(sectionTarget.NorthUnit));
                            if (eastComp < northComp)
                            {
                                continue;
                            }

                            var uA = (seg.A - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var uB = (seg.B - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var vA = (seg.A - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var vB = (seg.B - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var vLine = 0.5 * (vA + vB);
                            if (Math.Abs(Math.Abs(vLine) - primaryExpectedOffset) > offsetTol)
                            {
                                continue;
                            }

                            var minU = Math.Min(uA, uB) - uRangeTol;
                            var maxU = Math.Max(uA, uB) + uRangeTol;
                            var uGap = 0.0;
                            var targetU = endpointU;
                            if (endpointU < minU)
                            {
                                uGap = minU - endpointU;
                                targetU = minU;
                            }
                            else if (endpointU > maxU)
                            {
                                uGap = endpointU - maxU;
                                targetU = maxU;
                            }

                            if (uGap > maxUGapToProject)
                            {
                                continue;
                            }

                            var target = sectionTarget.SwCorner + (sectionTarget.EastUnit * targetU) + (sectionTarget.NorthUnit * vLine);
                            var southDelta = endpointV - vLine;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(vLine) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(target);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterUGap = uGap < (bestUGap - 1e-6);
                            var tiedUGap = Math.Abs(uGap - bestUGap) <= 1e-6;
                            var betterSouth = tiedUGap && southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = tiedUGap && Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterUGap && !betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestUGap = uGap;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = target;
                        }

                        return found;
                    }

                    bool TrySelectSouthTargetMidpointAnySection(
                        Point2d southEndpoint,
                        out Point2d targetMidpoint)
                    {
                        targetMidpoint = southEndpoint;
                        var found = false;
                        var bestSouthDelta = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < lsdSouthTargetMidpoints.Count; i++)
                        {
                            var target = lsdSouthTargetMidpoints[i];
                            var endpointU = (southEndpoint - target.SwCorner).DotProduct(target.EastUnit);
                            var endpointV = (southEndpoint - target.SwCorner).DotProduct(target.NorthUnit);
                            var targetU = (target.Midpoint - target.SwCorner).DotProduct(target.EastUnit);
                            var targetV = (target.Midpoint - target.SwCorner).DotProduct(target.NorthUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > seamCenterlineOffset)
                            {
                                continue;
                            }

                            var targetOffsetError = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset);
                            if (targetOffsetError > offsetTol)
                            {
                                continue;
                            }

                            var southDelta = endpointV - targetV;
                            if (southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            if (southDelta < -southwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointV) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetV) - primaryExpectedOffset) <= offsetTol;
                                var northwardMove = Math.Abs(southDelta);
                                var allowNorthwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    northwardMove <= maxNorthwardCorrection;
                                if (!allowNorthwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = southEndpoint.GetDistanceTo(target.Midpoint);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSouth = southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = target.Midpoint;
                        }

                        return found;
                    }

                    bool TrySelectSouthProjectionToTwentyTwelveAnySection(
                        Point2d southEndpoint,
                        out Point2d targetMidpoint)
                    {
                        targetMidpoint = southEndpoint;
                        var found = false;
                        var bestMoveDistance = double.MaxValue;
                        for (var si = 0; si < sectionTargets.Count; si++)
                        {
                            if (!TrySelectSouthProjectionToTwentyTwelve(southEndpoint, si, out var projected))
                            {
                                continue;
                            }

                            var move = southEndpoint.GetDistanceTo(projected);
                            if (move < (bestMoveDistance - 1e-6))
                            {
                                bestMoveDistance = move;
                                targetMidpoint = projected;
                                found = true;
                            }
                        }

                        return found;
                    }

                    bool TrySelectWestTargetMidpoint(
                        Point2d westEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint,
                        out bool usedFallback)
                    {
                        targetMidpoint = westEndpoint;
                        usedFallback = false;
                        var sectionTarget = sectionTargets[sectionIndex];
                        var endpointU = (westEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);

                        var found = false;
                        var bestSegDistance = double.MaxValue;
                        var bestMidDistance = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < lsdVerticalMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdVerticalMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var segDistance = DistancePointToSegment(westEndpoint, adj.OldA, adj.OldB);
                            if (segDistance > lsdOldSegmentTol)
                            {
                                continue;
                            }

                            var oldMidDistance = westEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdOldMidpointTol)
                            {
                                continue;
                            }

                            var targetU = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var westDelta = endpointU - targetU;
                            if (westDelta > maxWestwardDelta)
                            {
                                continue;
                            }

                            if (westDelta < -eastwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointU) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetU) - primaryExpectedOffset) <= offsetTol;
                                var eastwardMove = Math.Abs(westDelta);
                                var allowEastwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    eastwardMove <= maxEastwardCorrection;
                                if (!allowEastwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = westEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterSeg = segDistance < (bestSegDistance - 1e-6);
                            var tiedSeg = Math.Abs(segDistance - bestSegDistance) <= 1e-6;
                            var betterMid = tiedSeg && oldMidDistance < (bestMidDistance - 1e-6);
                            var tiedMid = tiedSeg && Math.Abs(oldMidDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && move < bestMoveDistance;
                            if (!betterSeg && !betterMid && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestSegDistance = segDistance;
                            bestMidDistance = oldMidDistance;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        if (found)
                        {
                            return true;
                        }

                        for (var i = 0; i < lsdVerticalMidpointAdjustments.Count; i++)
                        {
                            var adj = lsdVerticalMidpointAdjustments[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var oldMidDistance = westEndpoint.GetDistanceTo(adj.OldMid);
                            if (oldMidDistance > lsdFallbackOldMidpointTol)
                            {
                                continue;
                            }

                            var targetU = (adj.NewMid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var westDelta = endpointU - targetU;
                            if (westDelta > maxWestwardDelta)
                            {
                                continue;
                            }

                            if (westDelta < -eastwardTol)
                            {
                                var endpointAtThirtyEighteen = Math.Abs(Math.Abs(endpointU) - secondaryExpectedOffset) <= offsetTol;
                                var targetAtTwentyTwelve = Math.Abs(Math.Abs(targetU) - primaryExpectedOffset) <= offsetTol;
                                var eastwardMove = Math.Abs(westDelta);
                                var allowEastwardCorrection =
                                    endpointAtThirtyEighteen &&
                                    targetAtTwentyTwelve &&
                                    eastwardMove <= maxEastwardCorrection;
                                if (!allowEastwardCorrection)
                                {
                                    continue;
                                }
                            }

                            var move = westEndpoint.GetDistanceTo(adj.NewMid);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterMid = oldMidDistance < (bestMidDistance - 1e-6);
                            var tiedMid = Math.Abs(oldMidDistance - bestMidDistance) <= 1e-6;
                            var betterMove = tiedMid && move < bestMoveDistance;
                            if (!betterMid && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            usedFallback = true;
                            bestMidDistance = oldMidDistance;
                            bestMoveDistance = move;
                            targetMidpoint = adj.NewMid;
                        }

                        return found;
                    }

                    for (var i = 0; i < lsdSegments.Count; i++)
                    {
                        var lsd = lsdSegments[i];
                        lsdConsidered++;
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsAdjustableLsdLineSegment(p0, p1))
                        {
                            continue;
                        }

                        if (!TryGetOwningSectionIndex(p0, p1, out var sectionIndex))
                        {
                            lsdSkippedNoOwner++;
                            if (IsVerticalLike(p0, p1))
                            {
                                var moveStartNoOwner = p0.Y <= p1.Y;
                                var southEndpointNoOwner = moveStartNoOwner ? p0 : p1;
                                var usedProjectionAnySectionNoOwner = false;
                                if (!TrySelectSouthTargetMidpointAnySection(southEndpointNoOwner, out var targetAnySection))
                                {
                                    if (!TrySelectSouthProjectionToTwentyTwelveAnySection(southEndpointNoOwner, out targetAnySection))
                                    {
                                        targetAnySection = southEndpointNoOwner;
                                    }
                                    else
                                    {
                                        usedProjectionAnySectionNoOwner = true;
                                    }
                                }

                                if (targetAnySection.GetDistanceTo(southEndpointNoOwner) > endpointMoveTol)
                                {
                                    if (writableLsd is Line lsdLineNoOwner)
                                    {
                                        if (moveStartNoOwner)
                                        {
                                            lsdLineNoOwner.StartPoint = new Point3d(targetAnySection.X, targetAnySection.Y, lsdLineNoOwner.StartPoint.Z);
                                        }
                                        else
                                        {
                                            lsdLineNoOwner.EndPoint = new Point3d(targetAnySection.X, targetAnySection.Y, lsdLineNoOwner.EndPoint.Z);
                                        }
                                    }
                                    else if (writableLsd is Polyline lsdPolyNoOwner && !lsdPolyNoOwner.Closed && lsdPolyNoOwner.NumberOfVertices == 2)
                                    {
                                        lsdPolyNoOwner.SetPointAt(moveStartNoOwner ? 0 : 1, targetAnySection);
                                    }
                                    else
                                    {
                                        continue;
                                    }

                                    lsdAdjusted++;
                                    lsdFallbackEndpointAdjustments++;
                                    lsdOwnerlessSouthFallbackUsed++;
                                    if (usedProjectionAnySectionNoOwner)
                                    {
                                        lsdProjectionAnySectionFallbackUsed++;
                                    }
                                    continue;
                                }
                            }

                            if (!TryGetNearestSectionIndexFallback(p0, p1, out sectionIndex))
                            {
                                continue;
                            }

                            lsdOwnerFallbackUsed++;
                        }

                        var sectionTarget = sectionTargets[sectionIndex];
                        var moveStart = false;
                        var targetMid = default(Point2d);
                        var usedFallback = false;
                        if (IsVerticalLike(p0, p1))
                        {
                            var v0 = (p0 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var v1 = (p1 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            moveStart = v0 <= v1;
                            var southEndpoint = moveStart ? p0 : p1;
                            if (!TrySelectSouthTargetMidpoint(
                                southEndpoint,
                                sectionIndex,
                                out targetMid,
                                out usedFallback))
                            {
                                var usedAnySectionProjection = false;
                                if (!TrySelectSouthProjectionToTwentyTwelve(
                                    southEndpoint,
                                    sectionIndex,
                                    out targetMid))
                                {
                                    if (!TrySelectSouthProjectionToTwentyTwelveAnySection(
                                        southEndpoint,
                                        out targetMid))
                                    {
                                        lsdSkippedNoTarget++;
                                        continue;
                                    }

                                    lsdProjectionAnySectionFallbackUsed++;
                                    usedAnySectionProjection = true;
                                }

                                usedFallback = true;
                                if (!usedAnySectionProjection)
                                {
                                    lsdProjectionFallbackUsed++;
                                }
                            }
                        }
                        else if (IsHorizontalLike(p0, p1))
                        {
                            var u0 = (p0 - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var u1 = (p1 - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            moveStart = u0 <= u1;
                            var westEndpoint = moveStart ? p0 : p1;
                            if (!TrySelectWestTargetMidpoint(
                                westEndpoint,
                                sectionIndex,
                                out targetMid,
                                out usedFallback))
                            {
                                lsdSkippedNoTarget++;
                                continue;
                            }
                        }
                        else
                        {
                            lsdSkippedUnsupportedOrientation++;
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices == 2)
                        {
                            lsdPoly.SetPointAt(moveStart ? 0 : 1, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                        if (usedFallback)
                        {
                            lsdFallbackEndpointAdjustments++;
                        }
                    }

                    logger?.WriteLine(
                        $"Cleanup: SW 20.12 LSD diagnostics considered={lsdConsidered}, adjusted={lsdAdjusted}, ownerFallback={lsdOwnerFallbackUsed}, ownerlessSouthFallback={lsdOwnerlessSouthFallbackUsed}, projectionFallback={lsdProjectionFallbackUsed}, projectionAnySectionFallback={lsdProjectionAnySectionFallbackUsed}, skippedNoOwner={lsdSkippedNoOwner}, skippedNoTarget={lsdSkippedNoTarget}, skippedUnsupportedOrientation={lsdSkippedUnsupportedOrientation}.");
                }

                tr.Commit();
                if (connectedPairs > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: connected {connectedPairs} SW blind-line 20.12 pair(s) " +
                        $"(H={movedHorizontalEndpoints}, V={movedVerticalEndpoints}).");
                    logger?.WriteLine(
                        $"Cleanup: SW 20.12 forced-corner attempts={forcedCornerAttempts}, connected={forcedCornerConnected}.");
                    if (lsdAdjusted > 0)
                    {
                        logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of SW L-USEC 20.12 blind-line connection(s).");
                        if (lsdFallbackEndpointAdjustments > 0)
                        {
                            logger?.WriteLine($"Cleanup: {lsdFallbackEndpointAdjustments} SW L-SECTION-LSD endpoint(s) used nearest adjusted SW blind-line midpoint fallback.");
                        }
                    }
                }
                else
                {
                    logger?.WriteLine(
                        $"Cleanup: SW L-USEC 20.12 blind-line connect found no candidates " +
                        $"(sections={sectionTargets.Count}, withHV={sectionsWithCandidates}, pairs={pairCandidatesEvaluated}).");
                }
            }
        }

        private static void FinalSnapLsdEndpointsToTwentyTwelveMidpoints(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 120.0);
            if (clipWindows.Count == 0)
            {
                logger?.WriteLine("Cleanup: final 20.12 LSD midpoint snap skipped (no clip windows).");
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

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var twentyTwelveHorizontalMidpoints = new List<Point2d>();
                var twentyTwelveVerticalMidpoints = new List<Point2d>();
                var outerHorizontalSources = new List<(Point2d A, Point2d B)>();
                var outerVerticalSources = new List<(Point2d A, Point2d B)>();
                var roadAllowanceSegments = new List<(Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
                var generatedSeeds = new List<(Point2d A, Point2d B, bool Horizontal, bool Vertical)>();
                var lsdSegments = new List<ObjectId>();

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

                    if (string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsAdjustableLsdLineSegment(a, b))
                        {
                            lsdSegments.Add(id);
                        }

                        continue;
                    }

                    var isSecLike = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    if (!isSecLike)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    roadAllowanceSegments.Add((a, b, horizontal, vertical));
                    if (generatedSet.Contains(id))
                    {
                        generatedSeeds.Add((a, b, horizontal, vertical));
                        continue;
                    }

                    if (horizontal)
                    {
                        outerHorizontalSources.Add((a, b));
                    }
                    else if (vertical)
                    {
                        outerVerticalSources.Add((a, b));
                    }
                }

                bool AddUniqueMidpoint(List<Point2d> mids, Point2d candidate)
                {
                    const double duplicateTol = 0.10;
                    for (var mi = 0; mi < mids.Count; mi++)
                    {
                        if (mids[mi].GetDistanceTo(candidate) <= duplicateTol)
                        {
                            return false;
                        }
                    }

                    mids.Add(candidate);
                    return true;
                }

                bool TryExpandGeneratedSeed(
                    Point2d seedA,
                    Point2d seedB,
                    bool horizontal,
                    bool vertical,
                    out Point2d repA,
                    out Point2d repB)
                {
                    repA = seedA;
                    repB = seedB;
                    var seedLen = seedA.GetDistanceTo(seedB);
                    if (seedLen <= 1e-6)
                    {
                        return false;
                    }

                    const double collinearTol = 0.85;
                    const double overlapMin = 2.0;
                    var axis = (seedB - seedA) / seedLen;
                    var bestLen = seedLen;
                    var found = false;
                    for (var si = 0; si < roadAllowanceSegments.Count; si++)
                    {
                        var seg = roadAllowanceSegments[si];
                        if (horizontal != seg.Horizontal || vertical != seg.Vertical)
                        {
                            continue;
                        }

                        if (DistancePointToInfiniteLine(seg.A, seedA, seedB) > collinearTol ||
                            DistancePointToInfiniteLine(seg.B, seedA, seedB) > collinearTol)
                        {
                            continue;
                        }

                        var t0 = (seg.A - seedA).DotProduct(axis);
                        var t1 = (seg.B - seedA).DotProduct(axis);
                        var segMin = Math.Min(t0, t1);
                        var segMax = Math.Max(t0, t1);
                        var overlap = Math.Min(seedLen, segMax) - Math.Max(0.0, segMin);
                        if (overlap < overlapMin)
                        {
                            continue;
                        }

                        var len = seg.A.GetDistanceTo(seg.B);
                        if (len <= (bestLen + 1e-6))
                        {
                            continue;
                        }

                        bestLen = len;
                        repA = seg.A;
                        repB = seg.B;
                        found = true;
                    }

                    return found;
                }

                for (var gi = 0; gi < generatedSeeds.Count; gi++)
                {
                    var seed = generatedSeeds[gi];
                    var a = seed.A;
                    var b = seed.B;
                    if (TryExpandGeneratedSeed(seed.A, seed.B, seed.Horizontal, seed.Vertical, out var expandedA, out var expandedB))
                    {
                        a = expandedA;
                        b = expandedB;
                    }

                    var mid = Midpoint(a, b);
                    if (seed.Horizontal)
                    {
                        AddUniqueMidpoint(twentyTwelveHorizontalMidpoints, mid);
                    }
                    else if (seed.Vertical)
                    {
                        AddUniqueMidpoint(twentyTwelveVerticalMidpoints, mid);
                    }
                }

                if (lsdSegments.Count == 0 || roadAllowanceSegments.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine("Cleanup: final 20.12 LSD midpoint snap skipped (no LSD/finalized section targets).");
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double maxMove = 260.0;
                const double alignTol = 18.0;
                const double targetEndpointProximityTol = 18.0;
                const double supportDistanceTol = 2.0;
                const double supportDistanceTieTol = 0.05;
                const double minSupportSegmentLength = 8.0;
                var considered = 0;
                var adjusted = 0;
                var adjustedVertical = 0;
                var adjustedHorizontal = 0;
                var skippedNoTarget = 0;

                double MinDistanceToSources(Point2d p, List<(Point2d A, Point2d B)> sources)
                {
                    var best = double.MaxValue;
                    for (var si = 0; si < sources.Count; si++)
                    {
                        var src = sources[si];
                        var d = DistancePointToSegment(p, src.A, src.B);
                        if (d < best)
                        {
                            best = d;
                        }
                    }

                    return best;
                }

                double MinDistanceToMidpoints(Point2d p, List<Point2d> mids)
                {
                    var best = double.MaxValue;
                    for (var mi = 0; mi < mids.Count; mi++)
                    {
                        var d = p.GetDistanceTo(mids[mi]);
                        if (d < best)
                        {
                            best = d;
                        }
                    }

                    return best;
                }

                bool TryFindSupportingMidpoint(Point2d endpoint, bool useHorizontalSegments, out Point2d target, out double moveDistance)
                {
                    target = endpoint;
                    moveDistance = double.MaxValue;
                    var found = false;
                    var bestDist = double.MaxValue;
                    var bestLen = 0.0;
                    var bestMove = double.MaxValue;
                    for (var si = 0; si < roadAllowanceSegments.Count; si++)
                    {
                        var seg = roadAllowanceSegments[si];
                        if (useHorizontalSegments && !seg.Horizontal)
                        {
                            continue;
                        }

                        if (!useHorizontalSegments && !seg.Vertical)
                        {
                            continue;
                        }

                        var len = seg.A.GetDistanceTo(seg.B);
                        if (len < minSupportSegmentLength)
                        {
                            continue;
                        }

                        var distToSeg = DistancePointToSegment(endpoint, seg.A, seg.B);
                        if (distToSeg > supportDistanceTol)
                        {
                            continue;
                        }

                        var mid = Midpoint(seg.A, seg.B);
                        var move = endpoint.GetDistanceTo(mid);
                        if (move <= endpointMoveTol || move > maxMove)
                        {
                            continue;
                        }

                        var better = !found;
                        if (!better)
                        {
                            if (distToSeg < (bestDist - 1e-6))
                            {
                                better = true;
                            }
                            else if (Math.Abs(distToSeg - bestDist) <= supportDistanceTieTol &&
                                     (len > (bestLen + 1e-6) ||
                                      (Math.Abs(len - bestLen) <= 1e-6 && move < (bestMove - 1e-6))))
                            {
                                better = true;
                            }
                        }

                        if (!better)
                        {
                            continue;
                        }

                        found = true;
                        bestDist = distToSeg;
                        bestLen = len;
                        bestMove = move;
                        target = mid;
                        moveDistance = move;
                    }

                    return found;
                }

                for (var i = 0; i < lsdSegments.Count; i++)
                {
                    var id = lsdSegments[i];
                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableLsd, out var p0, out var p1) || !IsAdjustableLsdLineSegment(p0, p1))
                    {
                        continue;
                    }

                    considered++;
                    if (IsVerticalLike(p0, p1))
                    {
                        var startSupportFound = TryFindSupportingMidpoint(
                            p0,
                            useHorizontalSegments: true,
                            out var startSupportTarget,
                            out var startSupportMove);
                        var endSupportFound = TryFindSupportingMidpoint(
                            p1,
                            useHorizontalSegments: true,
                            out var endSupportTarget,
                            out var endSupportMove);
                        if (startSupportFound || endSupportFound)
                        {
                            var moveStartSupport = startSupportFound;
                            if (startSupportFound && endSupportFound)
                            {
                                var supportD0 = MinDistanceToSources(p0, outerHorizontalSources);
                                var supportD1 = MinDistanceToSources(p1, outerHorizontalSources);
                                if (Math.Abs(supportD0 - supportD1) > 1e-6)
                                {
                                    moveStartSupport = supportD0 <= supportD1;
                                }
                                else
                                {
                                    moveStartSupport = startSupportMove <= endSupportMove;
                                }
                            }

                            var supportTarget = moveStartSupport ? startSupportTarget : endSupportTarget;
                            if (TryMoveEndpoint(writableLsd, moveStartSupport, supportTarget, endpointMoveTol))
                            {
                                adjusted++;
                                adjustedVertical++;
                                continue;
                            }
                        }

                        var d0 = MinDistanceToSources(p0, outerHorizontalSources);
                        var d1 = MinDistanceToSources(p1, outerHorizontalSources);
                        var targetD0 = MinDistanceToMidpoints(p0, twentyTwelveHorizontalMidpoints);
                        var targetD1 = MinDistanceToMidpoints(p1, twentyTwelveHorizontalMidpoints);
                        if (!double.IsFinite(targetD0) || !double.IsFinite(targetD1))
                        {
                            skippedNoTarget++;
                            continue;
                        }

                        if (targetD0 > targetEndpointProximityTol && targetD1 > targetEndpointProximityTol)
                        {
                            continue;
                        }

                        var moveStart = targetD0 <= targetD1;
                        if (Math.Abs(targetD0 - targetD1) <= 1e-6)
                        {
                            moveStart = d0 <= d1;
                        }

                        var movingEndpoint = moveStart ? p0 : p1;
                        var found = false;
                        var bestAlign = double.MaxValue;
                        var bestMove = double.MaxValue;
                        var target = movingEndpoint;
                        for (var hi = 0; hi < twentyTwelveHorizontalMidpoints.Count; hi++)
                        {
                            var mid = twentyTwelveHorizontalMidpoints[hi];
                            var align = Math.Abs(mid.X - movingEndpoint.X);
                            if (align > alignTol)
                            {
                                continue;
                            }

                            var move = movingEndpoint.GetDistanceTo(mid);
                            if (move <= endpointMoveTol || move > maxMove)
                            {
                                continue;
                            }

                            if (align < (bestAlign - 1e-6) || (Math.Abs(align - bestAlign) <= 1e-6 && move < bestMove))
                            {
                                found = true;
                                bestAlign = align;
                                bestMove = move;
                                target = mid;
                            }
                        }

                        if (!found || !TryMoveEndpoint(writableLsd, moveStart, target, endpointMoveTol))
                        {
                            if (!found)
                            {
                                skippedNoTarget++;
                            }
                            continue;
                        }

                        adjusted++;
                        adjustedVertical++;
                    }
                    else if (IsHorizontalLike(p0, p1))
                    {
                        var moveStartEndpoint = TryFindSupportingMidpoint(
                            p0,
                            useHorizontalSegments: false,
                            out var startTarget,
                            out var startMove);
                        var moveEndEndpoint = TryFindSupportingMidpoint(
                            p1,
                            useHorizontalSegments: false,
                            out var endTarget,
                            out var endMove);

                        bool TryFindHorizontalEndpointTarget(Point2d endpoint, out Point2d target, out double bestMove)
                        {
                            target = endpoint;
                            bestMove = double.MaxValue;
                            var foundLocal = false;
                            var bestAlignLocal = double.MaxValue;
                            for (var vi = 0; vi < twentyTwelveVerticalMidpoints.Count; vi++)
                            {
                                var mid = twentyTwelveVerticalMidpoints[vi];
                                var align = Math.Abs(mid.Y - endpoint.Y);
                                if (align > alignTol)
                                {
                                    continue;
                                }

                                var move = endpoint.GetDistanceTo(mid);
                                if (move <= endpointMoveTol || move > maxMove)
                                {
                                    continue;
                                }

                                if (align < (bestAlignLocal - 1e-6) ||
                                    (Math.Abs(align - bestAlignLocal) <= 1e-6 && move < bestMove))
                                {
                                    foundLocal = true;
                                    bestAlignLocal = align;
                                    bestMove = move;
                                    target = mid;
                                }
                            }

                            return foundLocal;
                        }

                        if (!moveStartEndpoint && !moveEndEndpoint)
                        {
                            var targetD0 = MinDistanceToMidpoints(p0, twentyTwelveVerticalMidpoints);
                            var targetD1 = MinDistanceToMidpoints(p1, twentyTwelveVerticalMidpoints);
                            if (!double.IsFinite(targetD0) || !double.IsFinite(targetD1))
                            {
                                skippedNoTarget++;
                                continue;
                            }

                            if (targetD0 > targetEndpointProximityTol && targetD1 > targetEndpointProximityTol)
                            {
                                continue;
                            }

                            moveStartEndpoint = TryFindHorizontalEndpointTarget(p0, out startTarget, out startMove);
                            moveEndEndpoint = TryFindHorizontalEndpointTarget(p1, out endTarget, out endMove);
                        }

                        if (!moveStartEndpoint && !moveEndEndpoint)
                        {
                            skippedNoTarget++;
                            continue;
                        }

                        if (moveStartEndpoint && moveEndEndpoint &&
                            startTarget.GetDistanceTo(endTarget) < (MinAdjustableLsdLineLengthMeters * 0.60))
                        {
                            if (startMove <= endMove)
                            {
                                moveEndEndpoint = false;
                            }
                            else
                            {
                                moveStartEndpoint = false;
                            }
                        }

                        var movedAny = false;
                        if (moveStartEndpoint && TryMoveEndpoint(writableLsd, true, startTarget, endpointMoveTol))
                        {
                            adjusted++;
                            adjustedHorizontal++;
                            movedAny = true;
                        }

                        if (moveEndEndpoint && TryMoveEndpoint(writableLsd, false, endTarget, endpointMoveTol))
                        {
                            adjusted++;
                            adjustedHorizontal++;
                            movedAny = true;
                        }

                        if (!movedAny && !moveStartEndpoint && !moveEndEndpoint)
                        {
                            skippedNoTarget++;
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: final 20.12 LSD midpoint snap adjusted {adjusted} endpoint(s) (vertical={adjustedVertical}, horizontal={adjustedHorizontal}, considered={considered}, skippedNoTarget={skippedNoTarget}).");
            }
        }

        private static void ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(
            Database database,
            IReadOnlyList<string> searchFolders,
            IEnumerable<QuarterLabelInfo> labelQuarterInfos,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            // TODO(2026-02-09): SE L-USEC east-boundary extension still misses some cases;
            // revisit boundary candidate selection and downward target-horizontal matching.
            if (database == null ||
                searchFolders == null ||
                labelQuarterInfos == null ||
                generatedRoadAllowanceIds == null ||
                generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var targetInfos = labelQuarterInfos
                .Where(info =>
                    info != null &&
                    IsUsecSouthExtensionSection(info.SectionKey.Section))
                .ToList();
            if (targetInfos.Count == 0)
            {
                logger?.WriteLine("Cleanup: SE L-USEC south 20.12 connect skipped (no target section info).");
                return;
            }

            var targetSectionIds = targetInfos
                .Where(info => !info.SectionPolylineId.IsNull)
                .Select(info => info.SectionPolylineId)
                .Distinct()
                .ToList();
            var sectionKeyById = targetInfos
                .Where(info => info != null && !info.SectionPolylineId.IsNull)
                .GroupBy(info => info.SectionPolylineId)
                .ToDictionary(g => g.Key, g => g.First().SectionKey);

            var sectionTargets = new List<(ObjectId SectionId, Point2d SwCorner, Vector2d EastUnit, Vector2d NorthUnit, Extents3d Window, double EastEdgeU, Point2d OriginalSeCorner, bool HasOriginalSeCorner)>();
            var clipWindows = new List<Extents3d>();
            if (targetSectionIds.Count > 0)
            {
                const double seWindowBuffer = 120.0;
                var outlineLogger = logger ?? new Logger();
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    foreach (var sectionId in targetSectionIds)
                    {
                        if (!(tr.GetObject(sectionId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                        {
                            continue;
                        }

                        try
                        {
                            var ext = section.GeometricExtents;
                            if (!TryGetQuarterAnchors(section, out var sectionAnchors))
                            {
                                sectionAnchors = GetFallbackAnchors(section);
                            }

                            var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
                            var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));
                            Point2d swCorner;
                            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out swCorner))
                            {
                                swCorner = new Point2d(ext.MinPoint.X, ext.MinPoint.Y);
                            }

                            Point2d seCorner;
                            if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out seCorner))
                            {
                                seCorner = new Point2d(ext.MaxPoint.X, ext.MinPoint.Y);
                            }

                            var eastEdgeU = (seCorner - swCorner).DotProduct(eastUnit);
                            if (eastEdgeU <= 1e-6)
                            {
                                eastEdgeU = 0.0;
                                for (var vi = 0; vi < section.NumberOfVertices; vi++)
                                {
                                    var u = (section.GetPoint2dAt(vi) - swCorner).DotProduct(eastUnit);
                                    if (u > eastEdgeU)
                                    {
                                        eastEdgeU = u;
                                    }
                                }
                            }

                            var originalSeCorner = seCorner;
                            var hasOriginalSeCorner = false;
                            if (sectionKeyById.TryGetValue(sectionId, out var sectionKey) &&
                                TryLoadSectionOutline(searchFolders, sectionKey, outlineLogger, out var outline))
                            {
                                var sectionNumber = ParseSectionNumber(sectionKey.Section);
                                if (TryCreateSectionSpatialInfo(outline, sectionNumber, out var spatialInfo) &&
                                    spatialInfo != null)
                                {
                                    try
                                    {
                                        originalSeCorner = spatialInfo.SouthWest + (spatialInfo.EastUnit * spatialInfo.Width);
                                        hasOriginalSeCorner = true;
                                    }
                                    finally
                                    {
                                        spatialInfo.SectionPolyline.Dispose();
                                    }
                                }
                            }

                            var midX = 0.5 * (ext.MinPoint.X + ext.MaxPoint.X);
                            var midY = 0.5 * (ext.MinPoint.Y + ext.MaxPoint.Y);
                            var seWindow = new Extents3d(
                                new Point3d(midX - seWindowBuffer, ext.MinPoint.Y - seWindowBuffer, 0.0),
                                new Point3d(ext.MaxPoint.X + seWindowBuffer, midY + seWindowBuffer, 0.0));
                            clipWindows.Add(seWindow);
                            sectionTargets.Add((sectionId, swCorner, eastUnit, northUnit, seWindow, eastEdgeU, originalSeCorner, hasOriginalSeCorner));
                        }
                        catch
                        {
                        }
                    }

                    tr.Commit();
                }
            }

            if (clipWindows.Count == 0 || sectionTargets.Count == 0)
            {
                logger?.WriteLine("Cleanup: SE L-USEC south 20.12 connect skipped (no clip windows).");
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
                    var old = pl.GetPoint2dAt(moveStart ? 0 : 1);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(moveStart ? 0 : 1, target);
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

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                bool IsPointInWindow(Point2d p, Extents3d window)
                {
                    return p.X >= window.MinPoint.X && p.X <= window.MaxPoint.X &&
                           p.Y >= window.MinPoint.Y && p.Y <= window.MaxPoint.Y;
                }

                bool DoesSegmentIntersectWindow(Point2d a, Point2d b, Extents3d window)
                {
                    return IsPointInWindow(a, window) ||
                           IsPointInWindow(b, window) ||
                           TryClipSegmentToWindow(a, b, window, out _, out _);
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

                var usecHorizontals = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var originalVerticals = new List<(ObjectId Id, Point2d A, Point2d B)>();
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

                    var isUsecLayer = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSecLayer = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsecLayer && !isSecLayer)
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id) && IsHorizontalLike(a, b))
                    {
                        usecHorizontals.Add((id, a, b));
                    }

                    if (!generatedSet.Contains(id) && isUsecLayer && IsVerticalLike(a, b))
                    {
                        originalVerticals.Add((id, a, b));
                    }
                }

                if (usecHorizontals.Count == 0 || originalVerticals.Count == 0)
                {
                    tr.Commit();
                    logger?.WriteLine("Cleanup: SE L-USEC south 20.12 connect skipped (missing generated horizontals or original east boundaries).");
                    return;
                }

                const double endpointMoveTol = 0.05;
                const double minExtend = 0.10;
                const double maxExtend = 160.0;
                const double expectedSouthOffset = 10.06;
                const double southOffsetTol = 3.0;
                const double southBandMatchTol = 1.5;
                const double originalSeSnapBuffer = 11.0;
                const double eastEdgeWindowBack = 50.0;
                const double eastEdgeWindowForward = 120.0;
                const double horizontalSearchBack = 260.0;
                const double horizontalSearchForward = 80.0;
                var usedBoundaries = new HashSet<ObjectId>();
                var connected = 0;
                var sectionsWithOriginalBoundary = 0;
                var sectionsWithHorizontalCandidate = 0;
                var candidatesEvaluated = 0;
                var lsdTargetMidpoints = new List<(ObjectId SectionId, Point2d Midpoint)>();

                for (var si = 0; si < sectionTargets.Count; si++)
                {
                    var sectionTarget = sectionTargets[si];
                    var swCorner = sectionTarget.SwCorner;
                    var eastUnit = sectionTarget.EastUnit;
                    var northUnit = sectionTarget.NorthUnit;

                    double ToU(Point2d p) => (p - swCorner).DotProduct(eastUnit);
                    double ToV(Point2d p) => (p - swCorner).DotProduct(northUnit);

                    var eastBoundaryCandidates = new List<(
                        ObjectId Id,
                        Point2d A,
                        Point2d B,
                        double ULine,
                        bool ContainsSouthBand,
                        double SouthBandDistance,
                        bool NearOriginalSe,
                        double OriginalSeDistance)>();
                    for (var i = 0; i < originalVerticals.Count; i++)
                    {
                        var seg = originalVerticals[i];
                        if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                        {
                            continue;
                        }

                        var d = seg.B - seg.A;
                        var eastComp = Math.Abs(d.DotProduct(eastUnit));
                        var northComp = Math.Abs(d.DotProduct(northUnit));
                        if (northComp <= eastComp)
                        {
                            continue;
                        }

                        var uA = ToU(seg.A);
                        var uB = ToU(seg.B);
                        var vA = ToV(seg.A);
                        var vB = ToV(seg.B);
                        var uLine = 0.5 * (uA + uB);
                        if (uLine < (sectionTarget.EastEdgeU - eastEdgeWindowBack) ||
                            uLine > (sectionTarget.EastEdgeU + eastEdgeWindowForward))
                        {
                            continue;
                        }

                        var minV = Math.Min(vA, vB);
                        var maxV = Math.Max(vA, vB);
                        var southBandV = -expectedSouthOffset;
                        var containsSouthBand =
                            southBandV >= (minV - southBandMatchTol) &&
                            southBandV <= (maxV + southBandMatchTol);
                        var southBandDistance =
                            southBandV < minV
                                ? (minV - southBandV)
                                : southBandV > maxV
                                    ? (southBandV - maxV)
                                    : 0.0;

                        var southEndpointCandidate = vA <= vB ? seg.A : seg.B;
                        var originalSeDistance = sectionTarget.HasOriginalSeCorner
                            ? southEndpointCandidate.GetDistanceTo(sectionTarget.OriginalSeCorner)
                            : double.MaxValue;
                        var nearOriginalSe = sectionTarget.HasOriginalSeCorner &&
                                             originalSeDistance <= originalSeSnapBuffer;

                        eastBoundaryCandidates.Add((seg.Id, seg.A, seg.B, uLine, containsSouthBand, southBandDistance, nearOriginalSe, originalSeDistance));
                    }

                    if (eastBoundaryCandidates.Count == 0)
                    {
                        continue;
                    }

                    var availableBoundaryCandidates = eastBoundaryCandidates
                        .Where(c => !usedBoundaries.Contains(c.Id))
                        .ToList();
                    if (availableBoundaryCandidates.Count == 0)
                    {
                        continue;
                    }

                    if (sectionTarget.HasOriginalSeCorner)
                    {
                        var nearOriginalSeCandidates = availableBoundaryCandidates
                            .Where(c => c.NearOriginalSe)
                            .ToList();
                        if (nearOriginalSeCandidates.Count > 0)
                        {
                            availableBoundaryCandidates = nearOriginalSeCandidates;
                        }
                    }

                    var targetBoundary = availableBoundaryCandidates
                        .OrderBy(c => c.ContainsSouthBand ? 0 : 1)
                        .ThenBy(c => c.SouthBandDistance)
                        .ThenBy(c => c.OriginalSeDistance)
                        .ThenBy(c => c.ULine)
                        .FirstOrDefault();
                    if (targetBoundary.Id.IsNull)
                    {
                        continue;
                    }

                    sectionsWithOriginalBoundary++;
                    var boundaryVa = ToV(targetBoundary.A);
                    var boundaryVb = ToV(targetBoundary.B);
                    var southIsStart = boundaryVa <= boundaryVb;
                    var southPoint = southIsStart ? targetBoundary.A : targetBoundary.B;
                    var northPoint = southIsStart ? targetBoundary.B : targetBoundary.A;
                    var boundaryDir = northPoint - southPoint;
                    var boundaryLen = boundaryDir.Length;
                    if (boundaryLen <= 1e-6)
                    {
                        continue;
                    }

                    boundaryDir = boundaryDir / boundaryLen;
                    var boundaryU = targetBoundary.ULine;

                    var horizontalCandidates = new List<(
                        Point2d TargetPoint,
                        Point2d HorizontalMidpoint,
                        double Score)>();
                    for (var i = 0; i < usecHorizontals.Count; i++)
                    {
                        var seg = usecHorizontals[i];
                        if (!DoesSegmentIntersectWindow(seg.A, seg.B, sectionTarget.Window))
                        {
                            continue;
                        }

                        var d = seg.B - seg.A;
                        var eastComp = Math.Abs(d.DotProduct(eastUnit));
                        var northComp = Math.Abs(d.DotProduct(northUnit));
                        if (eastComp < northComp)
                        {
                            continue;
                        }

                        var uA = ToU(seg.A);
                        var uB = ToU(seg.B);
                        var minU = Math.Min(uA, uB);
                        var maxU = Math.Max(uA, uB);
                        var vA = ToV(seg.A);
                        var vB = ToV(seg.B);
                        var vLine = 0.5 * (vA + vB);
                        if (vLine > 2.0 || Math.Abs(vLine + expectedSouthOffset) > southOffsetTol)
                        {
                            continue;
                        }

                        var spansBoundary = boundaryU >= (minU - 1.0) && boundaryU <= (maxU + 1.0);

                        if (boundaryU < (sectionTarget.EastEdgeU - horizontalSearchBack) ||
                            boundaryU > (sectionTarget.EastEdgeU + horizontalSearchForward))
                        {
                            continue;
                        }

                        if (!spansBoundary && sectionTarget.HasOriginalSeCorner)
                        {
                            var seDist = DistancePointToSegment(sectionTarget.OriginalSeCorner, seg.A, seg.B);
                            if (seDist > originalSeSnapBuffer)
                            {
                                continue;
                            }
                        }

                        if (!TryIntersectInfiniteLines(
                            southPoint,
                            northPoint,
                            seg.A,
                            seg.B,
                            out var targetPoint))
                        {
                            continue;
                        }

                        if (!IsPointInWindow(targetPoint, sectionTarget.Window))
                        {
                            continue;
                        }

                        var tOnBoundary = (targetPoint - southPoint).DotProduct(boundaryDir);
                        if (tOnBoundary >= -minExtend)
                        {
                            continue;
                        }

                        var moveDist = southPoint.GetDistanceTo(targetPoint);
                        if (moveDist <= endpointMoveTol || moveDist > maxExtend)
                        {
                            continue;
                        }

                        var score = -tOnBoundary;
                        if (sectionTarget.HasOriginalSeCorner)
                        {
                            score += 0.25 * DistancePointToSegment(sectionTarget.OriginalSeCorner, seg.A, seg.B);
                        }

                        horizontalCandidates.Add((targetPoint, Midpoint(seg.A, seg.B), score));
                    }

                    candidatesEvaluated += horizontalCandidates.Count;
                    if (horizontalCandidates.Count == 0)
                    {
                        continue;
                    }

                    sectionsWithHorizontalCandidate++;
                    var best = horizontalCandidates
                        .OrderBy(c => c.Score)
                        .First();
                    if (!(tr.GetObject(targetBoundary.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryMoveEndpoint(writable, southIsStart, best.TargetPoint, endpointMoveTol))
                    {
                        continue;
                    }

                    lsdTargetMidpoints.Add((sectionTarget.SectionId, best.HorizontalMidpoint));

                    usedBoundaries.Add(targetBoundary.Id);
                    connected++;
                }

                var lsdAdjusted = 0;
                if (lsdTargetMidpoints.Count > 0)
                {
                    var lsdSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                        {
                            continue;
                        }

                        if (!string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
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

                        if (!IsAdjustableLsdLineSegment(a, b))
                        {
                            continue;
                        }

                        lsdSegments.Add((id, a, b));
                    }

                    const double lsdMaxMove = 80.0;
                    const double southwardTol = 0.50;
                    const double maxSouthwardDelta = 70.0;
                    const double maxCenterlineOffset = 35.0;

                    bool TryGetOwningSectionIndex(Point2d a, Point2d b, out int sectionIndex)
                    {
                        const double ownershipUTol = 40.0;
                        sectionIndex = -1;
                        var mid = Midpoint(a, b);
                        var bestDistance = double.MaxValue;
                        for (var si = 0; si < sectionTargets.Count; si++)
                        {
                            var sectionTarget = sectionTargets[si];
                            if (!DoesSegmentIntersectWindow(a, b, sectionTarget.Window) &&
                                !IsPointInWindow(mid, sectionTarget.Window))
                            {
                                continue;
                            }

                            var midU = (mid - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            if (midU < -ownershipUTol || midU > (sectionTarget.EastEdgeU + ownershipUTol))
                            {
                                continue;
                            }

                            var d = mid.GetDistanceTo(sectionTarget.SwCorner);
                            if (d < bestDistance)
                            {
                                bestDistance = d;
                                sectionIndex = si;
                            }
                        }

                        return sectionIndex >= 0;
                    }

                    bool TrySelectSouthTargetMidpoint(
                        Point2d southEndpoint,
                        int sectionIndex,
                        out Point2d targetMidpoint)
                    {
                        targetMidpoint = southEndpoint;
                        var sectionTarget = sectionTargets[sectionIndex];
                        var endpointU = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                        var endpointV = (southEndpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);

                        var found = false;
                        var bestCenterlineOffset = double.MaxValue;
                        var bestSouthDelta = double.MaxValue;
                        var bestMoveDistance = double.MaxValue;
                        for (var i = 0; i < lsdTargetMidpoints.Count; i++)
                        {
                            var adj = lsdTargetMidpoints[i];
                            if (adj.SectionId != sectionTarget.SectionId)
                            {
                                continue;
                            }

                            var targetU = (adj.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > maxCenterlineOffset)
                            {
                                continue;
                            }

                            var targetV = (adj.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var southDelta = endpointV - targetV;
                            if (southDelta < -southwardTol || southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            var move = southEndpoint.GetDistanceTo(adj.Midpoint);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterCenterline = centerlineOffset < (bestCenterlineOffset - 1e-6);
                            var tiedCenterline = Math.Abs(centerlineOffset - bestCenterlineOffset) <= 1e-6;
                            var betterSouth = tiedCenterline && southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = tiedCenterline && Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterCenterline && !betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestCenterlineOffset = centerlineOffset;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.Midpoint;
                        }

                        if (found)
                        {
                            return true;
                        }

                        // Fallback for section-association ambiguity near township seams:
                        // allow nearest SE-adjusted midpoint from neighboring target section.
                        for (var i = 0; i < lsdTargetMidpoints.Count; i++)
                        {
                            var adj = lsdTargetMidpoints[i];
                            var targetU = (adj.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.EastUnit);
                            var centerlineOffset = Math.Abs(endpointU - targetU);
                            if (centerlineOffset > (maxCenterlineOffset * 1.6))
                            {
                                continue;
                            }

                            var targetV = (adj.Midpoint - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                            var southDelta = endpointV - targetV;
                            if (southDelta < -southwardTol || southDelta > maxSouthwardDelta)
                            {
                                continue;
                            }

                            var move = southEndpoint.GetDistanceTo(adj.Midpoint);
                            if (move <= endpointMoveTol || move > lsdMaxMove)
                            {
                                continue;
                            }

                            var betterCenterline = centerlineOffset < (bestCenterlineOffset - 1e-6);
                            var tiedCenterline = Math.Abs(centerlineOffset - bestCenterlineOffset) <= 1e-6;
                            var betterSouth = tiedCenterline && southDelta < (bestSouthDelta - 1e-6);
                            var tiedSouth = tiedCenterline && Math.Abs(southDelta - bestSouthDelta) <= 1e-6;
                            var betterMove = tiedSouth && move < bestMoveDistance;
                            if (!betterCenterline && !betterSouth && !betterMove)
                            {
                                continue;
                            }

                            found = true;
                            bestCenterlineOffset = centerlineOffset;
                            bestSouthDelta = southDelta;
                            bestMoveDistance = move;
                            targetMidpoint = adj.Midpoint;
                        }

                        return found;
                    }

                    for (var i = 0; i < lsdSegments.Count; i++)
                    {
                        var lsd = lsdSegments[i];
                        if (!(tr.GetObject(lsd.Id, OpenMode.ForWrite, false) is Entity writableLsd) || writableLsd.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writableLsd, out var p0, out var p1))
                        {
                            continue;
                        }

                        if (!IsVerticalLike(p0, p1))
                        {
                            continue;
                        }

                        if (!TryGetOwningSectionIndex(p0, p1, out var sectionIndex))
                        {
                            continue;
                        }

                        var sectionTarget = sectionTargets[sectionIndex];
                        var v0 = (p0 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                        var v1 = (p1 - sectionTarget.SwCorner).DotProduct(sectionTarget.NorthUnit);
                        var moveStart = v0 <= v1;
                        var southEndpoint = moveStart ? p0 : p1;
                        if (!TrySelectSouthTargetMidpoint(southEndpoint, sectionIndex, out var targetMid))
                        {
                            continue;
                        }

                        if (writableLsd is Line lsdLine)
                        {
                            if (moveStart)
                            {
                                lsdLine.StartPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.StartPoint.Z);
                            }
                            else
                            {
                                lsdLine.EndPoint = new Point3d(targetMid.X, targetMid.Y, lsdLine.EndPoint.Z);
                            }
                        }
                        else if (writableLsd is Polyline lsdPoly && !lsdPoly.Closed && lsdPoly.NumberOfVertices >= 2)
                        {
                            var index = moveStart ? 0 : lsdPoly.NumberOfVertices - 1;
                            lsdPoly.SetPointAt(index, targetMid);
                        }
                        else
                        {
                            continue;
                        }

                        lsdAdjusted++;
                    }
                }

                tr.Commit();
                if (connected > 0)
                {
                    logger?.WriteLine($"Cleanup: connected {connected} SE L-USEC south 20.12 line(s) to west-most east RA original boundary.");
                    if (lsdAdjusted > 0)
                    {
                        logger?.WriteLine($"Cleanup: adjusted {lsdAdjusted} L-SECTION-LSD endpoint(s) to midpoint of SE L-USEC south 20.12 connection(s).");
                    }
                }
                else
                {
                    logger?.WriteLine(
                        $"Cleanup: SE L-USEC south 20.12 connect found no candidates " +
                        $"(sections={sectionTargets.Count}, withBoundary={sectionsWithOriginalBoundary}, withH={sectionsWithHorizontalCandidate}, candidates={candidatesEvaluated}).");
                }
            }

            // SE specific duplicate cleanup: equal-length blind-line twins can remain after SE connect.
            CleanupDuplicateBlindLineSegments(database, targetSectionIds, logger);
        }

private static List<Polyline> BuildBufferedQuarterOffsetPolylines(
            Database database,
            IEnumerable<ObjectId> quarterIds,
            double buffer,
            Logger? logger)
        {
            var result = new List<Polyline>();
            if (database == null || quarterIds == null || buffer <= 0.0)
            {
                return result;
            }

            var attempted = 0;
            var skippedInvalid = 0;
            var offsetFailed = 0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var id in quarterIds.Distinct())
                {
                    if (id.IsNull || id.IsErased)
                    {
                        skippedInvalid++;
                        continue;
                    }

                    var quarter = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (quarter == null || !quarter.Closed || quarter.NumberOfVertices < 3)
                    {
                        skippedInvalid++;
                        continue;
                    }

                    attempted++;
                    if (TryCreateOutsideOffsetPolyline(quarter, buffer, out var outside, logger))
                    {
                        result.Add(outside);
                    }
                    else
                    {
                        offsetFailed++;
                        logger?.WriteLine($"DEFPOINTS BUFFER: offset failed for quarter id {id}.");
                    }
                }

                tr.Commit();
            }

            logger?.WriteLine($"DEFPOINTS BUFFER: attempted={attempted}, created={result.Count}, skippedInvalid={skippedInvalid}, failed={offsetFailed}");
            return result;
        }

        private static bool TryCreateOutsideOffsetPolyline(Polyline source, double distance, [NotNullWhen(true)] out Polyline? outside, Logger? logger)
        {
            outside = null;
            var candidates = new List<Polyline>();
            try
            {
                CollectOffsetCandidates(source, distance, candidates);
                CollectOffsetCandidates(source, -distance, candidates);
                if (candidates.Count == 0)
                {
                    logger?.WriteLine("DEFPOINTS BUFFER: no offset candidates produced by GetOffsetCurves.");
                    return false;
                }

                Polyline? best = null;
                var bestArea = double.MinValue;
                foreach (var c in candidates)
                {
                    double area;
                    try { area = Math.Abs(c.Area); }
                    catch { area = 0.0; }
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = c;
                    }
                }

                if (best == null)
                {
                    logger?.WriteLine("DEFPOINTS BUFFER: candidates existed but no best offset selected.");
                    return false;
                }

                outside = (Polyline)best.Clone();
                logger?.WriteLine($"DEFPOINTS BUFFER: selected offset area={bestArea:0.###}, candidates={candidates.Count}");
                return true;
            }
            finally
            {
                foreach (var c in candidates)
                {
                    c.Dispose();
                }
            }
        }

        private static void CollectOffsetCandidates(Polyline source, double distance, List<Polyline> destination)
        {
            DBObjectCollection? offsets = null;
            try
            {
                offsets = source.GetOffsetCurves(distance);
                if (offsets == null)
                {
                    return;
                }

                foreach (DBObject obj in offsets)
                {
                    if (obj is Polyline pl && pl.Closed && pl.NumberOfVertices >= 3)
                    {
                        destination.Add((Polyline)pl.Clone());
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (offsets != null)
                {
                    foreach (DBObject obj in offsets)
                    {
                        obj.Dispose();
                    }
                }
            }
        }

        private static List<Polyline> BuildUnionBoundaries(List<Polyline> polylines, Logger? logger)
        {
            var output = new List<Polyline>();
            if (polylines == null || polylines.Count == 0)
            {
                return output;
            }

            Region? union = null;
            try
            {
                logger?.WriteLine($"DEFPOINTS BUFFER: union input count={polylines.Count}");
                foreach (var poly in polylines)
                {
                    var region = CreateRegionFromPolyline(poly);
                    if (region == null)
                    {
                        logger?.WriteLine("DEFPOINTS BUFFER: CreateRegionFromPolyline returned null for one offset.");
                        continue;
                    }

                    if (union == null)
                    {
                        union = region;
                    }
                    else
                    {
                        try
                        {
                            union.BooleanOperation(BooleanOperationType.BoolUnite, region);
                        }
                        finally
                        {
                            region.Dispose();
                        }
                    }
                }

                if (union == null)
                {
                    return output;
                }

                var exploded = new DBObjectCollection();
                union.Explode(exploded);
                var explodedCurves = new List<Curve>();
                foreach (DBObject obj in exploded)
                {
                    if (obj is Polyline pl && pl.Closed && pl.NumberOfVertices >= 3)
                    {
                        output.Add((Polyline)pl.Clone());
                    }
                    else if (obj is Curve curve)
                    {
                        explodedCurves.Add((Curve)curve.Clone());
                    }
                    obj.Dispose();
                }

                if (output.Count == 0)
                {
                    if (TryBuildClosedPolylinesFromCurves(explodedCurves, output))
                    {
                        logger?.WriteLine("DEFPOINTS BUFFER: union explode yielded curves; rebuilt closed boundary loop(s).");
                    }
                }

                foreach (var curve in explodedCurves)
                {
                    curve.Dispose();
                }

                if (output.Count == 0)
                {
                    // Fallback: keep the per-quarter offsets instead of collapsing into one large
                    // rectangle. This avoids pulling in unrelated neighboring sections.
                    foreach (var poly in polylines)
                    {
                        if (poly == null || !poly.Closed || poly.NumberOfVertices < 3)
                        {
                            continue;
                        }

                        output.Add((Polyline)poly.Clone());
                    }

                    if (output.Count > 0)
                    {
                        logger?.WriteLine("DEFPOINTS BUFFER: union explode empty, used per-offset fallback (no convex hull).");
                    }
                }
                logger?.WriteLine($"DEFPOINTS BUFFER: union output boundaries={output.Count}");
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("DEFPOINTS offset-union failed: " + ex.Message);
            }
            finally
            {
                union?.Dispose();
            }

            return output;
        }

        private static bool TryBuildClosedPolylinesFromCurves(List<Curve> curves, List<Polyline> output)
        {
            if (curves == null || curves.Count == 0 || output == null)
            {
                return false;
            }

            const double tol = 0.01;
            var segments = new List<(Point2d A, Point2d B)>();
            foreach (var curve in curves)
            {
                if (!(curve is Line line))
                {
                    continue;
                }

                var a = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                var b = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                if (a.GetDistanceTo(b) <= tol)
                {
                    continue;
                }

                segments.Add((a, b));
            }

            if (segments.Count < 3)
            {
                return false;
            }

            var builtAny = false;
            while (segments.Count > 0)
            {
                var loop = new List<Point2d>();
                var first = segments[0];
                segments.RemoveAt(0);
                loop.Add(first.A);
                loop.Add(first.B);

                var closed = false;
                while (true)
                {
                    var current = loop[loop.Count - 1];
                    if (current.GetDistanceTo(loop[0]) <= tol)
                    {
                        closed = true;
                        break;
                    }

                    var foundIndex = -1;
                    var next = default(Point2d);
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        if (current.GetDistanceTo(seg.A) <= tol)
                        {
                            foundIndex = i;
                            next = seg.B;
                            break;
                        }

                        if (current.GetDistanceTo(seg.B) <= tol)
                        {
                            foundIndex = i;
                            next = seg.A;
                            break;
                        }
                    }

                    if (foundIndex < 0)
                    {
                        break;
                    }

                    segments.RemoveAt(foundIndex);
                    loop.Add(next);
                }

                if (!closed || loop.Count < 4)
                {
                    continue;
                }

                var poly = new Polyline(loop.Count - 1) { Closed = true };
                for (var i = 0; i < loop.Count - 1; i++)
                {
                    poly.AddVertexAt(i, loop[i], 0, 0, 0);
                }

                if (poly.NumberOfVertices >= 3)
                {
                    output.Add(poly);
                    builtAny = true;
                }
                else
                {
                    poly.Dispose();
                }
            }

            return builtAny;
        }

        private static Polyline? BuildConvexHullPolyline(List<Polyline> polylines)
        {
            if (polylines == null || polylines.Count == 0)
            {
                return null;
            }

            var points = new List<Point2d>();
            foreach (var poly in polylines)
            {
                if (poly == null || poly.NumberOfVertices < 3)
                {
                    continue;
                }

                for (var i = 0; i < poly.NumberOfVertices; i++)
                {
                    points.Add(poly.GetPoint2dAt(i));
                }
            }

            var hull = ComputeConvexHull(points);
            if (hull.Count < 3)
            {
                return null;
            }

            var result = new Polyline(hull.Count) { Closed = true };
            for (var i = 0; i < hull.Count; i++)
            {
                result.AddVertexAt(i, hull[i], 0, 0, 0);
            }

            return result;
        }

        private static List<Point2d> ComputeConvexHull(List<Point2d> input)
        {
            var points = input
                .Where(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y))
                .Distinct(new Point2dApproxComparer(1e-6))
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (points.Count <= 1)
            {
                return points;
            }

            var lower = new List<Point2d>();
            foreach (var p in points)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0.0)
                {
                    lower.RemoveAt(lower.Count - 1);
                }
                lower.Add(p);
            }

            var upper = new List<Point2d>();
            for (var i = points.Count - 1; i >= 0; i--)
            {
                var p = points[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0.0)
                {
                    upper.RemoveAt(upper.Count - 1);
                }
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point2d a, Point2d b, Point2d c)
        {
            var ab = b - a;
            var ac = c - a;
            return (ab.X * ac.Y) - (ab.Y * ac.X);
        }

        private sealed class Point2dApproxComparer : IEqualityComparer<Point2d>
        {
            private readonly double _eps;

            public Point2dApproxComparer(double eps)
            {
                _eps = Math.Max(1e-9, eps);
            }

            public bool Equals(Point2d x, Point2d y)
            {
                return Math.Abs(x.X - y.X) <= _eps && Math.Abs(x.Y - y.Y) <= _eps;
            }

            public int GetHashCode(Point2d obj)
            {
                var qx = Math.Round(obj.X / _eps);
                var qy = Math.Round(obj.Y / _eps);
                return HashCode.Combine(qx, qy);
            }
        }

        private static Region? CreateRegionFromPolyline(Polyline polyline)
        {
            DBObjectCollection? curves = null;
            DBObjectCollection? regions = null;
            try
            {
                curves = new DBObjectCollection();
                curves.Add((Curve)polyline.Clone());
                regions = Region.CreateFromCurves(curves);
                if (regions == null || regions.Count == 0)
                {
                    return null;
                }

                var region = (Region)regions[0];
                for (var i = 1; i < regions.Count; i++)
                {
                    regions[i]?.Dispose();
                }
                return region;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (curves != null)
                {
                    foreach (DBObject c in curves)
                    {
                        c.Dispose();
                    }
                }
            }
        }

        private static List<Extents2d> MergeIntersectingExtents(List<Extents2d> input)
        {
            var remaining = new List<Extents2d>(input);
            var merged = new List<Extents2d>();
            while (remaining.Count > 0)
            {
                var current = remaining[0];
                remaining.RemoveAt(0);
                var expanded = true;
                while (expanded)
                {
                    expanded = false;
                    for (var i = remaining.Count - 1; i >= 0; i--)
                    {
                        var other = remaining[i];
                        if (!Extents2dIntersects(current, other))
                        {
                            continue;
                        }

                        current = UnionExtents2d(current, other);
                        remaining.RemoveAt(i);
                        expanded = true;
                    }
                }

                merged.Add(current);
            }

            return merged;
        }

        private static bool Extents2dIntersects(Extents2d a, Extents2d b)
        {
            return !(a.MaxPoint.X < b.MinPoint.X ||
                     a.MinPoint.X > b.MaxPoint.X ||
                     a.MaxPoint.Y < b.MinPoint.Y ||
                     a.MinPoint.Y > b.MaxPoint.Y);
        }

        private static Extents2d UnionExtents2d(Extents2d a, Extents2d b)
        {
            return new Extents2d(
                new Point2d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y)),
                new Point2d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y)));
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

        private static bool IsUsecSouthExtensionSection(string section)
        {
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

            if (!int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }

            return (n >= 1 && n <= 6) || (n >= 13 && n <= 18) || (n >= 25 && n <= 30);
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

        private static IReadOnlyList<string> BuildSectionIndexSearchFolders(Config config)
        {
            var folders = new List<string>();
            AddFolder(folders, config.SectionIndexFolder);
            AddFolder(folders, new Config().SectionIndexFolder);
            AddFolder(folders, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory);
            return folders;
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(folder);
            }
        }

        private static bool TryLoadSectionOutline(
            IReadOnlyList<string> searchFolders,
            SectionKey key,
            Logger logger,
            [NotNullWhen(true)] out SectionOutline? outline)
        {
            outline = null;
            var cacheKey = BuildSectionOutlineCacheKey(searchFolders, key);
            lock (SectionOutlineCacheLock)
            {
                if (SectionOutlineCache.TryGetValue(cacheKey, out var cached))
                {
                    outline = cached == null
                        ? null
                        : new SectionOutline(new List<Point2d>(cached.Vertices), cached.Closed, cached.SourcePath);
                    return outline != null;
                }
            }

            var checkedAny = false;
            foreach (var folder in searchFolders)
            {
                if (!FolderHasSectionIndex(folder, key.Zone))
                {
                    continue;
                }

                checkedAny = true;
                if (SectionIndexReader.TryLoadSectionOutline(folder, key, logger, out outline))
                {
                    lock (SectionOutlineCacheLock)
                    {
                        SectionOutlineCache[cacheKey] = new SectionOutline(new List<Point2d>(outline.Vertices), outline.Closed, outline.SourcePath);
                    }
                    return true;
                }
            }

            if (!checkedAny)
            {
                logger.WriteLine($"No section index file found for zone {key.Zone}. Searched: {string.Join("; ", searchFolders)}");
            }

            lock (SectionOutlineCacheLock)
            {
                SectionOutlineCache[cacheKey] = null;
            }
            return false;
        }

        private static bool FolderHasSectionIndex(string folder, int zone)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var cacheKey = string.Format(CultureInfo.InvariantCulture, "{0}|{1}", folder, zone);
            lock (SectionOutlineCacheLock)
            {
                if (FolderIndexCache.TryGetValue(cacheKey, out var existsCached))
                {
                    return existsCached;
                }
            }

            var jsonl = Path.Combine(folder, $"Master_Sections.index_Z{zone}.jsonl");
            var csv = Path.Combine(folder, $"Master_Sections.index_Z{zone}.csv");
            var jsonlFallback = Path.Combine(folder, "Master_Sections.index.jsonl");
            var csvFallback = Path.Combine(folder, "Master_Sections.index.csv");
            var exists = File.Exists(jsonl) || File.Exists(csv) || File.Exists(jsonlFallback) || File.Exists(csvFallback);
            lock (SectionOutlineCacheLock)
            {
                FolderIndexCache[cacheKey] = exists;
            }

            return exists;
        }

        private static string BuildSectionOutlineCacheKey(IReadOnlyList<string> searchFolders, SectionKey key)
        {
            var foldersKey = searchFolders == null
                ? string.Empty
                : string.Join(";", searchFolders.Select(f => (f ?? string.Empty).Trim()).Where(f => f.Length > 0));

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}",
                key.Zone,
                NormalizeNumberToken(key.Meridian),
                NormalizeNumberToken(key.Range),
                NormalizeNumberToken(key.Township),
                NormalizeNumberToken(key.Section),
                foldersKey);
        }

        private struct P3ImportSummary
        {
            public int ImportedEntities;
            public int FilteredEntities;
            public int ImportFailures;
        }

        private static P3ImportSummary ImportP3Shapefiles(
            Database database,
            Editor editor,
            Logger logger,
            IReadOnlyList<ObjectId> sectionPolylineIds)
        {
            const string p3Folder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\P3";
            const string outputLayer = "T-WATER-P3";
            const double sectionBuffer = 100.0;
            var shapefiles = new[]
            {
                "BF_Hydro_Polygon.shp",
                "BF_SLNET_arc.shp"
            };

            var summary = new P3ImportSummary();
            var sectionExtents = BuildSectionExtents(database, sectionPolylineIds, sectionBuffer);
            if (sectionExtents.Count == 0)
            {
                logger?.WriteLine("P3 import skipped: no section extents.");
                return summary;
            }

            Importer? importer = null;
            try
            {
                importer = HostMapApplicationServices.Application?.Importer;
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("P3 importer unavailable: " + ex.Message);
            }

            if (importer == null)
            {
                summary.ImportFailures += shapefiles.Length;
                return summary;
            }

            foreach (var fileName in shapefiles)
            {
                var path = Path.Combine(p3Folder, fileName);
                if (!File.Exists(path))
                {
                    logger?.WriteLine("P3 shapefile missing: " + path);
                    summary.ImportFailures++;
                    continue;
                }

                var beforeIds = CaptureModelSpaceEntityIds(database);
                try
                {
                    importer.Init("SHP", path);
                    TrySetImporterLocationWindow(importer, sectionExtents, logger);
                    foreach (InputLayer layer in importer)
                    {
                        layer.ImportFromInputLayerOn = true;
                    }

                    importer.Import();
                }
                catch (System.Exception ex)
                {
                    logger?.WriteLine("P3 import failed for " + path + ": " + ex.Message);
                    summary.ImportFailures++;
                    continue;
                }

                var afterIds = CaptureModelSpaceEntityIds(database);
                var newIds = afterIds.Where(id => !beforeIds.Contains(id)).ToList();

                using (var tr = database.TransactionManager.StartTransaction())
                {
                    EnsureLayer(database, tr, outputLayer);

                    foreach (var id in newIds)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (ent == null || ent.IsErased)
                        {
                            continue;
                        }

                        if (!IsEntityInsideAnySectionExtents(ent, sectionExtents))
                        {
                            ent.Erase(true);
                            summary.FilteredEntities++;
                            continue;
                        }

                        ent.Layer = outputLayer;
                        ent.ColorIndex = 256;
                        summary.ImportedEntities++;
                    }

                    tr.Commit();
                }
            }

            editor?.WriteMessage($"\nImported {summary.ImportedEntities} P3 entities.");
            return summary;
        }

        private static HashSet<ObjectId> CaptureModelSpaceEntityIds(Database database)
        {
            var ids = new HashSet<ObjectId>();
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    ids.Add(id);
                }

                tr.Commit();
            }

            return ids;
        }

        private static List<Extents2d> BuildSectionExtents(
            Database database,
            IReadOnlyList<ObjectId> sectionPolylineIds,
            double buffer)
        {
            var extents = new List<Extents2d>();
            if (database == null || sectionPolylineIds == null || sectionPolylineIds.Count == 0)
            {
                return extents;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var id in sectionPolylineIds)
                {
                    if (id.IsNull)
                    {
                        continue;
                    }

                    var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null)
                    {
                        continue;
                    }

                    try
                    {
                        var ge = ent.GeometricExtents;
                        extents.Add(new Extents2d(
                            ge.MinPoint.X - buffer,
                            ge.MinPoint.Y - buffer,
                            ge.MaxPoint.X + buffer,
                            ge.MaxPoint.Y + buffer));
                    }
                    catch
                    {
                        // ignore invalid extents
                    }
                }

                tr.Commit();
            }

            return extents;
        }

        private static bool IsEntityInsideAnySectionExtents(Entity ent, List<Extents2d> sectionExtents)
        {
            if (ent == null || sectionExtents == null || sectionExtents.Count == 0)
            {
                return false;
            }

            Extents3d ge;
            try
            {
                ge = ent.GeometricExtents;
            }
            catch
            {
                return false;
            }

            var e2d = new Extents2d(ge.MinPoint.X, ge.MinPoint.Y, ge.MaxPoint.X, ge.MaxPoint.Y);
            foreach (var sectionExtent in sectionExtents)
            {
                if (!(e2d.MaxPoint.X < sectionExtent.MinPoint.X ||
                      e2d.MinPoint.X > sectionExtent.MaxPoint.X ||
                      e2d.MaxPoint.Y < sectionExtent.MinPoint.Y ||
                      e2d.MinPoint.Y > sectionExtent.MaxPoint.Y))
                {
                    return true;
                }
            }

            return false;
        }

        private static void TrySetImporterLocationWindow(Importer importer, List<Extents2d> sectionExtents, Logger? logger)
        {
            if (importer == null || sectionExtents == null || sectionExtents.Count == 0)
            {
                return;
            }

            var union = UnionExtents(sectionExtents);
            try
            {
                var method = importer.GetType().GetMethod("SetLocationWindowAndOptions");
                if (method == null)
                {
                    return;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 5 || !parameters[4].ParameterType.IsEnum)
                {
                    return;
                }

                var option = GetEnumValue(parameters[4].ParameterType, 2, "kUseLocationWindow", "UseLocationWindow");
                method.Invoke(importer, new object[]
                {
                    union.MinPoint.X,
                    union.MaxPoint.X,
                    union.MinPoint.Y,
                    union.MaxPoint.Y,
                    option
                });
                logger?.WriteLine($"P3 importer location window set: X[{union.MinPoint.X:G},{union.MaxPoint.X:G}] Y[{union.MinPoint.Y:G},{union.MaxPoint.Y:G}]");
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("P3 location window setup failed: " + ex.Message);
            }
        }

        private static Extents2d UnionExtents(List<Extents2d> extents)
        {
            var minX = extents[0].MinPoint.X;
            var minY = extents[0].MinPoint.Y;
            var maxX = extents[0].MaxPoint.X;
            var maxY = extents[0].MaxPoint.Y;

            for (var i = 1; i < extents.Count; i++)
            {
                var e = extents[i];
                minX = Math.Min(minX, e.MinPoint.X);
                minY = Math.Min(minY, e.MinPoint.Y);
                maxX = Math.Max(maxX, e.MaxPoint.X);
                maxY = Math.Max(maxY, e.MaxPoint.Y);
            }

            return new Extents2d(minX, minY, maxX, maxY);
        }

        private static object GetEnumValue(Type enumType, int fallbackNumeric, params string[] names)
        {
            foreach (var name in names)
            {
                try
                {
                    return Enum.Parse(enumType, name, true);
                }
                catch
                {
                    // keep trying
                }
            }

            return Enum.ToObject(enumType, fallbackNumeric);
        }

        private static void EnsureLayer(Database database, Transaction transaction, string layerName)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName
            };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void SetLayerVisibility(
            Database database,
            Transaction transaction,
            string layerName,
            bool isOff,
            bool isPlottable)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!table.Has(layerName))
            {
                return;
            }

            var layerId = table[layerName];
            var layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForWrite);
            layer.IsOff = isOff;
            layer.IsPlottable = isPlottable;
        }

        private static ObjectId InsertSectionLabelBlock(
            BlockTableRecord modelSpace,
            BlockTable blockTable,
            Transaction transaction,
            Editor editor,
            Point3d position,
            SectionKey key)
        {
            const string blockName = "L-SECLBL";

            if (!blockTable.Has(blockName))
            {
                editor?.WriteMessage($"\nBUILDSEC: Block '{blockName}' not found; skipped section label.");
                return ObjectId.Null;
            }

            var blockId = blockTable[blockName];
            var blockRef = new BlockReference(position, blockId)
            {
                ScaleFactors = new Scale3d(1.0)
            };
            var blockRefId = modelSpace.AppendEntity(blockRef);
            transaction.AddNewlyCreatedDBObject(blockRef, true);

            var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (blockDef.HasAttributeDefinitions)
            {
                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition definition))
                    {
                        continue;
                    }

                    if (definition.Constant)
                    {
                        continue;
                    }

                    var reference = new AttributeReference();
                    reference.SetAttributeFromBlock(definition, blockRef.BlockTransform);
                    blockRef.AttributeCollection.AppendAttribute(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                }
            }

            SetBlockAttribute(blockRef, transaction, "SEC", key.Section);
            SetBlockAttribute(blockRef, transaction, "TWP", key.Township);
            SetBlockAttribute(blockRef, transaction, "RGE", key.Range);
            SetBlockAttribute(blockRef, transaction, "MER", key.Meridian);

            return blockRefId;
        }

        private static void SetBlockAttribute(BlockReference blockRef, Transaction transaction, string tag, string value)
        {
            if (blockRef == null || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            foreach (ObjectId id in blockRef.AttributeCollection)
            {
                if (!(transaction.GetObject(id, OpenMode.ForWrite) is AttributeReference attr))
                {
                    continue;
                }

                if (string.Equals(attr.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    attr.TextString = value ?? string.Empty;
                }
            }
        }

        private static Point2d ResolveSectionLabelPosition(
            Polyline sectionPolyline,
            Dictionary<QuarterSelection, Polyline> quarterMap,
            QuarterAnchors anchors,
            List<LineSegment2d> lineworkBarriers,
            BlockTable blockTable,
            Transaction transaction,
            bool drawLsds,
            Point2d sectionCenter)
        {
            if (sectionPolyline == null)
            {
                return sectionCenter;
            }

            if (!TryGetSectionLabelBlockFootprint(blockTable, transaction, out var labelWidth, out var labelHeight))
            {
                labelWidth = 130.0;
                labelHeight = 70.0;
            }

            var requiredClearance = Math.Sqrt((labelWidth * 0.5 * labelWidth * 0.5) + (labelHeight * 0.5 * labelHeight * 0.5)) + 2.0;

            bool Fits(Point2d p)
            {
                if (!IsLabelBoxInsideSection(sectionPolyline, p, labelWidth, labelHeight))
                    return false;

                return GetLineworkClearance(p, lineworkBarriers) >= requiredClearance;
            }

            if (!drawLsds)
            {
                if (Fits(sectionCenter))
                {
                    return sectionCenter;
                }

                if (TryFindNonOverlapSectionPosition(sectionPolyline, sectionCenter, labelWidth, labelHeight, requiredClearance, lineworkBarriers, out var candidate))
                {
                    return candidate;
                }

                return GetLeastCongestedQuarterCenter(quarterMap, sectionPolyline, lineworkBarriers, sectionCenter);
            }

            if (TryFindNonOverlapSectionPosition(sectionPolyline, sectionCenter, labelWidth, labelHeight, requiredClearance, lineworkBarriers, out var openArea))
            {
                return openArea;
            }

            return GetLeastCongestedLsdCenter(sectionPolyline, anchors, lineworkBarriers, sectionCenter);
        }

        private static bool TryGetSectionLabelBlockFootprint(
            BlockTable blockTable,
            Transaction transaction,
            out double width,
            out double height)
        {
            width = 0;
            height = 0;

            const string blockName = "L-SECLBL";
            if (blockTable == null || transaction == null || !blockTable.Has(blockName))
            {
                return false;
            }

            try
            {
                var blockId = blockTable[blockName];
                var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);

                var found = false;
                var minX = 0.0;
                var minY = 0.0;
                var maxX = 0.0;
                var maxY = 0.0;

                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity))
                    {
                        continue;
                    }

                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!found)
                    {
                        minX = extents.MinPoint.X;
                        minY = extents.MinPoint.Y;
                        maxX = extents.MaxPoint.X;
                        maxY = extents.MaxPoint.Y;
                        found = true;
                    }
                    else
                    {
                        minX = Math.Min(minX, extents.MinPoint.X);
                        minY = Math.Min(minY, extents.MinPoint.Y);
                        maxX = Math.Max(maxX, extents.MaxPoint.X);
                        maxY = Math.Max(maxY, extents.MaxPoint.Y);
                    }
                }

                if (!found)
                {
                    return false;
                }

                width = Math.Max(1.0, maxX - minX);
                height = Math.Max(1.0, maxY - minY);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindNonOverlapSectionPosition(
            Polyline sectionPolyline,
            Point2d preferredCenter,
            double labelWidth,
            double labelHeight,
            double requiredClearance,
            List<LineSegment2d> lineworkBarriers,
            out Point2d location)
        {
            location = preferredCenter;
            var step = Math.Max(5.0, Math.Min(labelWidth, labelHeight) * 0.6);
            foreach (var p in GeometryUtils.GetSpiralOffsets(preferredCenter, step, 400))
            {
                if (!IsLabelBoxInsideSection(sectionPolyline, p, labelWidth, labelHeight))
                {
                    continue;
                }

                if (GetLineworkClearance(p, lineworkBarriers) >= requiredClearance)
                {
                    location = p;
                    return true;
                }
            }

            return false;
        }

        private static Point2d GetLeastCongestedQuarterCenter(
            Dictionary<QuarterSelection, Polyline> quarterMap,
            Polyline sectionPolyline,
            List<LineSegment2d> lineworkBarriers,
            Point2d fallback)
        {
            if (quarterMap == null || quarterMap.Count == 0)
            {
                return fallback;
            }

            var bestPoint = fallback;
            var bestScore = double.NegativeInfinity;

            foreach (var quarter in quarterMap.Values)
            {
                if (quarter == null)
                {
                    continue;
                }

                var p = GeometryUtils.GetSafeInteriorPoint(quarter);
                if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, p))
                {
                    continue;
                }

                var score = GetLineworkClearance(p, lineworkBarriers);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = p;
                }
            }

            return bestPoint;
        }

        private static Point2d GetLeastCongestedLsdCenter(
            Polyline sectionPolyline,
            QuarterAnchors anchors,
            List<LineSegment2d> lineworkBarriers,
            Point2d fallback)
        {
            var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
            var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
            var width = anchors.Left.GetDistanceTo(anchors.Right);
            var height = anchors.Bottom.GetDistanceTo(anchors.Top);
            if (width <= 1e-6 || height <= 1e-6)
            {
                return fallback;
            }

            var southWest = fallback - (eastUnit * (width * 0.5)) - (northUnit * (height * 0.5));
            var bestPoint = fallback;
            var bestScore = double.NegativeInfinity;

            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    var p = southWest +
                            (eastUnit * (width * ((col + 0.5) / 4.0))) +
                            (northUnit * (height * ((row + 0.5) / 4.0)));
                    if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, p))
                    {
                        continue;
                    }

                    var score = GetLineworkClearance(p, lineworkBarriers);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPoint = p;
                    }
                }
            }

            return bestPoint;
        }

        private static Point2d GetLeastCongestedPointInBoundary(
            Polyline boundary,
            List<LineSegment2d> lineworkBarriers,
            Point2d fallback)
        {
            if (boundary == null)
            {
                return fallback;
            }

            var seed = GeometryUtils.GetSafeInteriorPoint(boundary);
            var bestPoint = GeometryUtils.IsPointInsidePolyline(boundary, seed) ? seed : fallback;
            var bestScore = GetLineworkClearance(bestPoint, lineworkBarriers);

            var extents = boundary.GeometricExtents;
            var diag = extents.MaxPoint.DistanceTo(extents.MinPoint);
            var step = Math.Max(5.0, diag / 16.0);

            foreach (var p in GeometryUtils.GetSpiralOffsets(bestPoint, step, 220))
            {
                if (!GeometryUtils.IsPointInsidePolyline(boundary, p))
                {
                    continue;
                }

                var score = GetLineworkClearance(p, lineworkBarriers);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = p;
                }
            }

            return bestPoint;
        }

        private static bool TryGetBestQuarterLsdCellCenter(
            Polyline quarterPolyline,
            Vector2d eastUnit,
            Vector2d northUnit,
            List<LineSegment2d> lineworkBarriers,
            Func<Point2d, bool> fitsPredicate,
            out Point2d bestCellCenter,
            out Point2d? bestFitCellCenter)
        {
            bestCellCenter = default;
            bestFitCellCenter = null;
            if (quarterPolyline == null)
            {
                return false;
            }

            var anchors = GetLsdAnchorsForQuarter(quarterPolyline, eastUnit, northUnit);
            var width = anchors.Left.GetDistanceTo(anchors.Right);
            var height = anchors.Bottom.GetDistanceTo(anchors.Top);
            if (width <= 1e-6 || height <= 1e-6)
            {
                return false;
            }

            var quarterCenter = new Point2d(
                0.5 * (anchors.Top.X + anchors.Bottom.X),
                0.5 * (anchors.Left.Y + anchors.Right.Y));
            var southWest = quarterCenter - (eastUnit * (width * 0.5)) - (northUnit * (height * 0.5));

            var haveAny = false;
            var bestAnyScore = double.NegativeInfinity;
            var bestFitScore = double.NegativeInfinity;

            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    var p = southWest +
                            (eastUnit * (width * ((col + 0.5) / 2.0))) +
                            (northUnit * (height * ((row + 0.5) / 2.0)));
                    if (!GeometryUtils.IsPointInsidePolyline(quarterPolyline, p))
                    {
                        continue;
                    }

                    var score = GetLineworkClearance(p, lineworkBarriers);
                    if (!haveAny || score > bestAnyScore)
                    {
                        bestAnyScore = score;
                        bestCellCenter = p;
                        haveAny = true;
                    }

                    if (fitsPredicate != null && fitsPredicate(p) && score > bestFitScore)
                    {
                        bestFitScore = score;
                        bestFitCellCenter = p;
                    }
                }
            }

            return haveAny;
        }

        private static bool IsLabelBoxInsideSection(Polyline sectionPolyline, Point2d center, double width, double height)
        {
            var halfW = width * 0.5;
            var halfH = height * 0.5;
            var points = new[]
            {
                center,
                new Point2d(center.X - halfW, center.Y - halfH),
                new Point2d(center.X + halfW, center.Y - halfH),
                new Point2d(center.X - halfW, center.Y + halfH),
                new Point2d(center.X + halfW, center.Y + halfH),
                new Point2d(center.X, center.Y - halfH),
                new Point2d(center.X, center.Y + halfH),
                new Point2d(center.X - halfW, center.Y),
                new Point2d(center.X + halfW, center.Y)
            };

            foreach (var p in points)
            {
                if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, p))
                {
                    return false;
                }
            }

            return true;
        }

        private static double GetLineworkClearance(Point2d point, List<LineSegment2d> lineworkBarriers)
        {
            if (lineworkBarriers == null || lineworkBarriers.Count == 0)
            {
                return double.MaxValue;
            }

            var min = double.MaxValue;
            foreach (var segment in lineworkBarriers)
            {
                var d = DistancePointToSegment(point, segment.StartPoint, segment.EndPoint);
                if (d < min)
                {
                    min = d;
                }
            }

            return min;
        }

        private static double DistancePointToSegment(Point2d point, Point2d a, Point2d b)
        {
            var ab = b - a;
            var len2 = ab.DotProduct(ab);
            if (len2 <= 1e-12)
            {
                return point.GetDistanceTo(a);
            }

            var ap = point - a;
            var t = ap.DotProduct(ab) / len2;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            var nearest = new Point2d(a.X + ab.X * t, a.Y + ab.Y * t);
            return point.GetDistanceTo(nearest);
        }

        private static void AddPolylineSegments(List<LineSegment2d> destination, Polyline polyline)
        {
            if (destination == null || polyline == null || polyline.NumberOfVertices < 2)
            {
                return;
            }

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var j = i + 1;
                if (j >= polyline.NumberOfVertices)
                {
                    if (!polyline.Closed)
                    {
                        break;
                    }

                    j = 0;
                }

                var a = polyline.GetPoint2dAt(i);
                var b = polyline.GetPoint2dAt(j);
                if (a.GetDistanceTo(b) > 1e-9)
                {
                    destination.Add(new LineSegment2d(a, b));
                }
            }
        }

        private struct QuarterAnchors
        {
            public QuarterAnchors(Point2d top, Point2d bottom, Point2d left, Point2d right)
            {
                Top = top;
                Bottom = bottom;
                Left = left;
                Right = right;
            }

            public Point2d Top { get; }
            public Point2d Bottom { get; }
            public Point2d Left { get; }
            public Point2d Right { get; }
        }

        private struct EdgeInfo
        {
            public int Index;
            public Point3d A;
            public Point3d B;
            public Point3d Mid;
            public Vector3d U;
            public double Len;
        }

        private struct ChainInfo
        {
            public int Start;
            public int SegCount;
            public double Score;
            public double TotalLen;
        }

        // ------------------------------------------------------------------
        // PLSR XML check
        // ------------------------------------------------------------------

        private sealed class PlsrActivity
        {
            public string DispNum { get; set; } = string.Empty;
            public string Owner { get; set; } = string.Empty;
            public DateTime? ExpiryDate { get; set; }
        }

        private sealed class PlsrQuarterData
        {
            public DateTime ReportDate { get; set; }
            public List<PlsrActivity> Activities { get; } = new List<PlsrActivity>();
        }

        private sealed class PlsrLabelEntry
        {
            public ObjectId Id { get; set; }
            public bool IsLeader { get; set; }
            public bool IsDimension { get; set; }
            public string Owner { get; set; } = string.Empty;
            public string DispNum { get; set; } = string.Empty;
            public string RawContents { get; set; } = string.Empty;
            public Point2d Location { get; set; }
        }

        private static readonly HashSet<string> PlsrDispositionPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LOC","PLA","MSL","MLL","DML","EZE","PIL","RME","RML","DLO","ROE","RRD","DPI","DPL","VCE","DRS","SML","SME"
        };

        private static void RunPlsrCheck(
            Database database,
            Editor editor,
            Logger logger,
            ExcelLookup companyLookup,
            AtsBuildInput input,
            List<QuarterInfo> quarters)
        {
            if (input.PlsrXmlPaths == null || input.PlsrXmlPaths.Count == 0)
            {
                editor.WriteMessage("\nPLSR check skipped: no XML files selected.");
                logger.WriteLine("PLSR check skipped: no XML files selected.");
                return;
            }

            var notIncludedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var quarterData = LoadPlsrQuarterData(input.PlsrXmlPaths, logger, notIncludedPrefixes);

            var requestedQuarterKeys = BuildRequestedQuarterKeys(input.SectionRequests);
            var missingQuarterKeys = requestedQuarterKeys.Where(k => !quarterData.ContainsKey(k)).ToList();

            var labelByQuarter = CollectPlsrLabels(database, quarters, logger);

            var summary = new StringBuilder();
            summary.AppendLine("PLSR Check Summary");
            summary.AppendLine("-------------------");

            int missingLabels = 0;
            int ownerMismatches = 0;
            int extraLabels = 0;
            int expiredTagged = 0;

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var quarterKey in requestedQuarterKeys)
                {
                    labelByQuarter.TryGetValue(quarterKey, out var labels);
                    quarterData.TryGetValue(quarterKey, out var expected);

                    labels ??= new List<PlsrLabelEntry>();
                    var expectedActivities = expected?.Activities ?? new List<PlsrActivity>();

                    var expectedByDisp = new Dictionary<string, PlsrActivity>(StringComparer.OrdinalIgnoreCase);
                    foreach (var act in expectedActivities)
                    {
                        var normDisp = NormalizeDispNum(act.DispNum);
                        if (string.IsNullOrWhiteSpace(normDisp))
                            continue;
                        if (!expectedByDisp.ContainsKey(normDisp))
                            expectedByDisp.Add(normDisp, act);
                    }

                    var labelByDisp = new Dictionary<string, PlsrLabelEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var label in labels)
                    {
                        var normDisp = NormalizeDispNum(label.DispNum);
                        if (string.IsNullOrWhiteSpace(normDisp))
                            continue;

                        var prefix = GetDispositionPrefix(normDisp);
                        if (string.IsNullOrWhiteSpace(prefix))
                            continue;

                        if (!PlsrDispositionPrefixes.Contains(prefix))
                        {
                            notIncludedPrefixes.Add(prefix);
                            continue;
                        }

                        if (!labelByDisp.ContainsKey(normDisp))
                            labelByDisp.Add(normDisp, label);
                    }

                    foreach (var pair in expectedByDisp)
                    {
                        var dispNum = pair.Key;
                        var act = pair.Value;
                        var prefix = GetDispositionPrefix(dispNum);
                        if (string.IsNullOrWhiteSpace(prefix))
                            continue;

                        if (!PlsrDispositionPrefixes.Contains(prefix))
                        {
                            notIncludedPrefixes.Add(prefix);
                            continue;
                        }

                        if (!labelByDisp.TryGetValue(dispNum, out var label))
                        {
                            missingLabels++;
                            summary.AppendLine($"Missing label: {dispNum} in {quarterKey}");
                            continue;
                        }

                        var labelOwner = NormalizeOwner(label.Owner);
                        var expectedOwner = NormalizeOwner(MapClientNameForCompare(companyLookup, act.Owner));
                        if (!string.Equals(labelOwner, expectedOwner, StringComparison.OrdinalIgnoreCase))
                        {
                            ownerMismatches++;
                            summary.AppendLine($"Owner mismatch: {dispNum} in {quarterKey} (label='{label.Owner}' vs xml='{act.Owner}')");
                        }

                        if (expected != null && act.ExpiryDate.HasValue && act.ExpiryDate.Value < expected.ReportDate)
                        {
                            if (TryApplyExpiredMarker(tr, label, out var updated))
                            {
                                expiredTagged++;
                                if (updated)
                                {
                                    // already tagged
                                }
                            }
                        }
                    }

                    foreach (var labelPair in labelByDisp)
                    {
                        if (!expectedByDisp.ContainsKey(labelPair.Key))
                        {
                            extraLabels++;
                            summary.AppendLine($"Not in PLSR: {labelPair.Key} in {quarterKey}");
                        }
                    }
                }

                tr.Commit();
            }

            if (missingQuarterKeys.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Quarters requested but not found in XML:");
                foreach (var q in missingQuarterKeys)
                    summary.AppendLine($"- {q}");
            }

            if (notIncludedPrefixes.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Not Included in check: " + string.Join(", ", notIncludedPrefixes.OrderBy(p => p)));
            }

            summary.AppendLine();
            summary.AppendLine($"Missing labels: {missingLabels}");
            summary.AppendLine($"Owner mismatches: {ownerMismatches}");
            summary.AppendLine($"Extra labels not in PLSR: {extraLabels}");
            summary.AppendLine($"Expired tags added: {expiredTagged}");

            var summaryText = summary.ToString().TrimEnd();
            try
            {
                System.Windows.Forms.MessageBox.Show(summaryText, "PLSR Check", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch
            {
                editor.WriteMessage("\n" + summaryText);
            }

            WritePlsrLog(database, summaryText, logger);
        }

        private static Dictionary<string, PlsrQuarterData> LoadPlsrQuarterData(
            IEnumerable<string> xmlPaths,
            Logger logger,
            HashSet<string> notIncludedPrefixes)
        {
            var result = new Dictionary<string, PlsrQuarterData>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in xmlPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    logger.WriteLine("PLSR XML missing: " + path);
                    continue;
                }

                if (!TryParsePlsrXml(path, logger, out var reportDate, out var activities))
                {
                    logger.WriteLine("PLSR XML parse failed: " + path);
                    continue;
                }

                var reportQuarterActivities = new Dictionary<string, List<PlsrActivity>>(StringComparer.OrdinalIgnoreCase);
                foreach (var activity in activities)
                {
                    var prefix = GetDispositionPrefix(NormalizeDispNum(activity.Item1.DispNum));
                    if (!string.IsNullOrWhiteSpace(prefix) && !PlsrDispositionPrefixes.Contains(prefix))
                    {
                        notIncludedPrefixes.Add(prefix);
                        continue;
                    }

                    foreach (var landId in activity.Item2)
                    {
                        foreach (var quarterKey in BuildQuarterKeysFromLandId(landId))
                        {
                            if (!reportQuarterActivities.TryGetValue(quarterKey, out var list))
                            {
                                list = new List<PlsrActivity>();
                                reportQuarterActivities[quarterKey] = list;
                            }

                            list.Add(activity.Item1);
                        }
                    }
                }

                foreach (var pair in reportQuarterActivities)
                {
                    if (!result.TryGetValue(pair.Key, out var existing) || reportDate > existing.ReportDate)
                    {
                        var data = new PlsrQuarterData { ReportDate = reportDate };
                        data.Activities.AddRange(pair.Value);
                        result[pair.Key] = data;
                    }
                    else if (reportDate == existing.ReportDate)
                    {
                        existing.Activities.AddRange(pair.Value);
                    }
                }
            }

            return result;
        }

        private static bool TryParsePlsrXml(
            string path,
            Logger logger,
            out DateTime reportDate,
            out List<(PlsrActivity, List<string>)> activities)
        {
            reportDate = DateTime.MinValue;
            activities = new List<(PlsrActivity, List<string>)>();

            try
            {
                var doc = XDocument.Load(path);
                if (doc.Root == null)
                    return false;

                XNamespace ns = "urn:srd.gov.ab.ca:glimps:data:reports";

                var reportDateText = doc.Root.Element(ns + "ReportRunDate")?.Value;
                if (!DateTime.TryParse(reportDateText, out reportDate))
                    reportDate = DateTime.MinValue;

                var activitiesElement = doc.Root.Element(ns + "Activities");
                if (activitiesElement == null)
                    return true;

                foreach (var activity in activitiesElement.Elements(ns + "Activity"))
                {
                    var dispNum = activity.Element(ns + "ActivityNumber")?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(dispNum))
                        continue;

                    var owner = activity.Element(ns + "ServiceClientName")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(owner))
                    {
                        owner = activity
                            .Element(ns + "Clients")?
                            .Elements(ns + "ActivityClient")
                            .Select(c => c.Element(ns + "ClientName")?.Value?.Trim())
                            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                    }

                    owner ??= string.Empty;

                    DateTime? expiryDate = null;
                    var expiryText = activity.Element(ns + "ExpiryDate")?.Value?.Trim();
                    if (DateTime.TryParse(expiryText, out var expiryParsed))
                        expiryDate = expiryParsed;

                    var landIds = new List<string>();
                    var lands = activity.Element(ns + "Lands");
                    if (lands != null)
                    {
                        foreach (var land in lands.Elements(ns + "ActivityLand"))
                        {
                            var landId = land.Element(ns + "LandId")?.Value?.Trim();
                            if (!string.IsNullOrWhiteSpace(landId))
                                landIds.Add(landId);
                        }
                    }

                    if (landIds.Count == 0)
                        continue;

                    activities.Add((new PlsrActivity
                    {
                        DispNum = dispNum,
                        Owner = owner,
                        ExpiryDate = expiryDate
                    }, landIds));
                }

                return true;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR XML read failed: " + ex.Message);
                return false;
            }
        }

        private static List<string> BuildQuarterKeysFromLandId(string landId)
        {
            var keys = new List<string>();
            if (!TryParseLandId(landId, out var meridian, out var range, out var township, out var section, out var quarter))
                return keys;

            if (string.IsNullOrWhiteSpace(quarter))
            {
                keys.Add(BuildQuarterKey(meridian, range, township, section, "NW"));
                keys.Add(BuildQuarterKey(meridian, range, township, section, "NE"));
                keys.Add(BuildQuarterKey(meridian, range, township, section, "SW"));
                keys.Add(BuildQuarterKey(meridian, range, township, section, "SE"));
                return keys;
            }

            keys.Add(BuildQuarterKey(meridian, range, township, section, quarter));
            return keys;
        }

        private static bool TryParseLandId(
            string landId,
            out string meridian,
            out string range,
            out string township,
            out string section,
            out string? quarter)
        {
            meridian = string.Empty;
            range = string.Empty;
            township = string.Empty;
            section = string.Empty;
            quarter = null;

            if (string.IsNullOrWhiteSpace(landId))
                return false;

            var tokens = landId.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4)
                return false;

            meridian = NormalizeMeridianToken(tokens[0]);
            range = NormalizeNumberToken(tokens[1]);
            township = NormalizeNumberToken(tokens[2]);
            section = NormalizeNumberToken(tokens[3]);

            if (tokens.Length >= 5)
            {
                var last = tokens[tokens.Length - 1].Trim().ToUpperInvariant();
                if (IsQuarterToken(last))
                {
                    quarter = last;
                }
            }

            return true;
        }

        private static bool IsQuarterToken(string token)
        {
            return token == "NW" || token == "NE" || token == "SW" || token == "SE";
        }

        private static List<string> BuildRequestedQuarterKeys(IEnumerable<SectionRequest> requests)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                foreach (var quarter in ExpandQuarterSelections(request.Quarter))
                {
                    var key = BuildQuarterKey(request.Key, quarter);
                    if (!string.IsNullOrWhiteSpace(key))
                        keys.Add(key);
                }
            }

            return keys.ToList();
        }

        private static IEnumerable<QuarterSelection> ExpandQuarterSelections(QuarterSelection selection)
        {
            switch (selection)
            {
                case QuarterSelection.NorthHalf:
                    return new[] { QuarterSelection.NorthWest, QuarterSelection.NorthEast };
                case QuarterSelection.SouthHalf:
                    return new[] { QuarterSelection.SouthWest, QuarterSelection.SouthEast };
                case QuarterSelection.WestHalf:
                    return new[] { QuarterSelection.NorthWest, QuarterSelection.SouthWest };
                case QuarterSelection.EastHalf:
                    return new[] { QuarterSelection.NorthEast, QuarterSelection.SouthEast };
                case QuarterSelection.All:
                    return new[]
                    {
                        QuarterSelection.NorthWest,
                        QuarterSelection.NorthEast,
                        QuarterSelection.SouthWest,
                        QuarterSelection.SouthEast
                    };
                case QuarterSelection.NorthWest:
                case QuarterSelection.NorthEast:
                case QuarterSelection.SouthWest:
                case QuarterSelection.SouthEast:
                    return new[] { selection };
                default:
                    return Array.Empty<QuarterSelection>();
            }
        }

        private static string BuildQuarterKey(SectionKey key, QuarterSelection quarter)
        {
            var meridian = NormalizeMeridianToken(key.Meridian);
            var range = NormalizeNumberToken(key.Range);
            var township = NormalizeNumberToken(key.Township);
            var section = NormalizeNumberToken(key.Section);
            var q = QuarterSelectionToToken(quarter);
            if (string.IsNullOrWhiteSpace(q))
                return string.Empty;
            return BuildQuarterKey(meridian, range, township, section, q);
        }

        private static string BuildQuarterKey(string meridian, string range, string township, string section, string quarter)
        {
            return $"{meridian}|{range}|{township}|{section}|{quarter}";
        }

        private static string QuarterSelectionToToken(QuarterSelection quarter)
        {
            return quarter switch
            {
                QuarterSelection.NorthWest => "NW",
                QuarterSelection.NorthEast => "NE",
                QuarterSelection.SouthWest => "SW",
                QuarterSelection.SouthEast => "SE",
                _ => string.Empty
            };
        }

        private static string NormalizeMeridianToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var num))
                return num.ToString();

            return token.Trim().ToUpperInvariant();
        }

        private static string NormalizeNumberToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            if (int.TryParse(token.Trim(), out var num))
                return num.ToString();

            return token.Trim().TrimStart('0');
        }

        private static Dictionary<string, List<PlsrLabelEntry>> CollectPlsrLabels(Database database, List<QuarterInfo> quarters, Logger logger)
        {
            var byQuarter = new Dictionary<string, List<PlsrLabelEntry>>(StringComparer.OrdinalIgnoreCase);
            if (quarters == null || quarters.Count == 0)
                return byQuarter;

            var quarterMap = new Dictionary<string, QuarterInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in quarters)
            {
                if (q.SectionKey == null || q.Quarter == QuarterSelection.None)
                    continue;
                var key = BuildQuarterKey(q.SectionKey.Value, q.Quarter);
                if (!quarterMap.ContainsKey(key))
                    quarterMap.Add(key, q);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    PlsrLabelEntry? entry = null;

                    var dbObject = tr.GetObject(id, OpenMode.ForRead);
                    if (dbObject is MText mtext)
                    {
                        var contents = mtext.Contents ?? string.Empty;
                        entry = BuildLabelEntry(id, false, contents, new Point2d(mtext.Location.X, mtext.Location.Y));
                    }
                    else if (dbObject is MLeader mleader)
                    {
                        var leaderText = mleader.MText;
                        if (leaderText != null)
                        {
                            var contents = leaderText.Contents ?? string.Empty;
                            var anchor = GetLeaderAnchorPoint(mleader, leaderText, logger);
                            entry = BuildLabelEntry(id, true, contents, anchor);
                        }
                    }
                    else if (dbObject is AlignedDimension aligned)
                    {
                        var contents = aligned.DimensionText ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(contents) && !string.Equals(contents.Trim(), "<>", StringComparison.Ordinal))
                        {
                            var location = GetDimensionAnchorPoint(aligned);
                            entry = BuildLabelEntry(id, false, contents, location, isDimension: true);
                        }
                    }

                    if (entry == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(entry.DispNum) || string.IsNullOrWhiteSpace(entry.Owner))
                        continue;

                    var prefix = GetDispositionPrefix(NormalizeDispNum(entry.DispNum));
                    if (string.IsNullOrWhiteSpace(prefix) || !PlsrDispositionPrefixes.Contains(prefix))
                        continue;

                    bool assigned = false;
                    foreach (var pair in quarterMap)
                    {
                        if (GeometryUtils.IsPointInsidePolyline(pair.Value.Polyline, entry.Location))
                        {
                            if (!byQuarter.TryGetValue(pair.Key, out var list))
                            {
                                list = new List<PlsrLabelEntry>();
                                byQuarter[pair.Key] = list;
                            }

                            list.Add(entry);
                            assigned = true;
                            break;
                        }
                    }

                    _ = assigned;
                }

                tr.Commit();
            }
            return byQuarter;
        }

        private static PlsrLabelEntry? BuildLabelEntry(ObjectId id, bool isLeader, string contents, Point2d location, bool isDimension = false)
        {
            var lines = SplitMTextLines(contents);
            if (lines.Count < 2)
                return null;

            var owner = lines.FirstOrDefault() ?? string.Empty;
            var dispNum = lines.LastOrDefault() ?? string.Empty;

            return new PlsrLabelEntry
            {
                Id = id,
                IsLeader = isLeader,
                IsDimension = isDimension,
                Owner = owner,
                DispNum = dispNum,
                RawContents = contents,
                Location = location
            };
        }

        private static Point2d GetDimensionAnchorPoint(AlignedDimension dimension)
        {
            try
            {
                var textPos = dimension.TextPosition;
                return new Point2d(textPos.X, textPos.Y);
            }
            catch
            {
                // fallback below
            }

            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
            }
            catch
            {
                return new Point2d(0.0, 0.0);
            }
        }

        private static Point2d GetLeaderAnchorPoint(MLeader leader, MText leaderText, Logger logger)
        {
            try
            {
                var type = leader.GetType();
                var allVertices = new List<Point3d>();
                string? source = null;

                void AddVertices(Point3dCollection? pts, string src)
                {
                    if (pts == null || pts.Count == 0)
                        return;
                    if (source == null)
                        source = src;
                    foreach (Point3d p in pts)
                        allVertices.Add(p);
                }

                int? TryGetInt(string name)
                {
                    var prop = type.GetProperty(name);
                    if (prop != null && prop.PropertyType == typeof(int))
                        return (int?)prop.GetValue(leader);
                    var method = type.GetMethod(name, Type.EmptyTypes);
                    if (method != null && method.ReturnType == typeof(int))
                        return (int?)method.Invoke(leader, Array.Empty<object>());
                    return null;
                }

                int? leaderCount = TryGetInt("NumLeaders") ?? TryGetInt("LeaderCount") ?? TryGetInt("NumberOfLeaders");
                var maxLeaders = leaderCount.HasValue ? Math.Max(1, leaderCount.Value) : 4;

                var method2 = type.GetMethod("GetLeaderLineVertices", new[] { typeof(int), typeof(int) });
                var method1 = type.GetMethod("GetLeaderLineVertices", new[] { typeof(int) });
                var method3 = type.GetMethod("GetLeaderLineVertices", new[] { typeof(int), typeof(int), typeof(bool) });

                var getLeaderIndexes = type.GetMethod("GetLeaderIndexes", Type.EmptyTypes);
                var getLeaderLineIndexes = type.GetMethod("GetLeaderLineIndexes", new[] { typeof(int) });
                var getFirstVertex = type.GetMethods().FirstOrDefault(m => m.Name == "GetFirstVertex");
                var getLastVertex = type.GetMethods().FirstOrDefault(m => m.Name == "GetLastVertex");
                var getVertex = type.GetMethods().FirstOrDefault(m => m.Name == "GetVertex");

                int? TryGetLineCount(int leaderIndex)
                {
                    var method = type.GetMethod("GetLeaderLineCount", new[] { typeof(int) });
                    if (method != null && method.ReturnType == typeof(int))
                        return (int?)method.Invoke(leader, new object[] { leaderIndex });
                    var prop = type.GetProperty("LeaderLineCount");
                    if (prop != null && prop.PropertyType == typeof(int))
                        return (int?)prop.GetValue(leader);
                    return null;
                }

                IEnumerable<int> EnumerateLeaderIndexes()
                {
                    if (getLeaderIndexes != null)
                    {
                        var result = getLeaderIndexes.Invoke(leader, Array.Empty<object>());
                        if (result is IEnumerable<int> ints)
                            return ints;
                        if (result is System.Collections.IEnumerable enumerable)
                            return enumerable.Cast<object>().Select(o => Convert.ToInt32(o));
                    }
                    return Enumerable.Range(0, maxLeaders);
                }

                IEnumerable<int> EnumerateLeaderLineIndexes(int leaderIndex, int maxLines)
                {
                    if (getLeaderLineIndexes != null)
                    {
                        var result = getLeaderLineIndexes.Invoke(leader, new object[] { leaderIndex });
                        if (result is IEnumerable<int> ints)
                            return ints;
                        if (result is System.Collections.IEnumerable enumerable)
                            return enumerable.Cast<object>().Select(o => Convert.ToInt32(o));
                    }
                    return Enumerable.Range(0, maxLines);
                }

                Point3d? TryInvokePoint3d(MethodInfo? method, params object[] args)
                {
                    if (method == null)
                        return null;
                    try
                    {
                        var paramCount = method.GetParameters().Length;
                        if (paramCount != args.Length)
                            return null;
                        var result = method.Invoke(leader, args);
                        if (result is Point3d p)
                            return p;
                    }
                    catch
                    {
                        // ignore
                    }
                    return null;
                }

                foreach (int leaderIndex in EnumerateLeaderIndexes())
                {
                    int? lineCount = TryGetLineCount(leaderIndex);
                    var maxLines = lineCount.HasValue ? Math.Max(1, lineCount.Value) : 4;

                    foreach (int lineIndex in EnumerateLeaderLineIndexes(leaderIndex, maxLines))
                    {
                        if (method2 != null)
                        {
                            var result = method2.Invoke(leader, new object[] { leaderIndex, lineIndex }) as Point3dCollection;
                            AddVertices(result, "GetLeaderLineVertices(int,int)");
                        }

                        if (method3 != null)
                        {
                            var resultFalse = method3.Invoke(leader, new object[] { leaderIndex, lineIndex, false }) as Point3dCollection;
                            AddVertices(resultFalse, "GetLeaderLineVertices(int,int,bool=false)");
                            var resultTrue = method3.Invoke(leader, new object[] { leaderIndex, lineIndex, true }) as Point3dCollection;
                            AddVertices(resultTrue, "GetLeaderLineVertices(int,int,bool=true)");
                        }

                        var first = TryInvokePoint3d(getFirstVertex, leaderIndex, lineIndex)
                            ?? TryInvokePoint3d(getFirstVertex, leaderIndex)
                            ?? TryInvokePoint3d(getFirstVertex, lineIndex);
                        if (first.HasValue)
                            AddVertices(new Point3dCollection { first.Value }, "GetFirstVertex");

                        var last = TryInvokePoint3d(getLastVertex, leaderIndex, lineIndex)
                            ?? TryInvokePoint3d(getLastVertex, leaderIndex)
                            ?? TryInvokePoint3d(getLastVertex, lineIndex);
                        if (last.HasValue)
                            AddVertices(new Point3dCollection { last.Value }, "GetLastVertex");

                        if (getVertex != null)
                        {
                            for (int v = 0; v < 6; v++)
                            {
                                var vtx = TryInvokePoint3d(getVertex, leaderIndex, lineIndex, v)
                                    ?? TryInvokePoint3d(getVertex, leaderIndex, v)
                                    ?? TryInvokePoint3d(getVertex, lineIndex, v);
                                if (vtx.HasValue)
                                    AddVertices(new Point3dCollection { vtx.Value }, "GetVertex");
                            }
                        }
                    }

                    if (method1 != null)
                    {
                        var result = method1.Invoke(leader, new object[] { leaderIndex }) as Point3dCollection;
                        AddVertices(result, "GetLeaderLineVertices(int)");
                    }
                }

                if (allVertices.Count == 0 && method1 != null)
                {
                    var result = method1.Invoke(leader, new object[] { 0 }) as Point3dCollection;
                    AddVertices(result, "GetLeaderLineVertices(int)@0");
                }

                var prop = type.GetProperty("LeaderLineVertices");
                if (prop != null)
                {
                    var val = prop.GetValue(leader);
                    if (val is Point3dCollection pts)
                        AddVertices(pts, "LeaderLineVertices");
                    else if (val is IEnumerable<Point3d> enumerable)
                    {
                        var pts2 = new Point3dCollection();
                        foreach (var p in enumerable)
                            pts2.Add(p);
                        AddVertices(pts2, "LeaderLineVertices(IEnumerable)");
                    }
                }

                if (allVertices.Count > 0)
                    return SelectLeaderHeadPoint(allVertices, leaderText.Location);
            }
            catch
            {
                // fall back to text location
            }

            return new Point2d(leaderText.Location.X, leaderText.Location.Y);
        }

        private static Point2d SelectLeaderHeadPoint(Point3dCollection vertices, Point3d labelLocation)
        {
            double bestDistance = double.MinValue;
            Point3d best = labelLocation;
            foreach (Point3d p in vertices)
            {
                double d = p.DistanceTo(labelLocation);
                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }

            return new Point2d(best.X, best.Y);
        }

        private static Point2d SelectLeaderHeadPoint(IEnumerable<Point3d> vertices, Point3d labelLocation)
        {
            double bestDistance = double.MinValue;
            Point3d best = labelLocation;
            foreach (Point3d p in vertices)
            {
                double d = p.DistanceTo(labelLocation);
                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }

            return new Point2d(best.X, best.Y);
        }

        private static List<string> SplitMTextLines(string contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
                return new List<string>();

            var normalized = contents
                .Replace("\\P", "\n")
                .Replace("\\X", "\n")
                .Replace("\r", "\n");
            var raw = normalized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            foreach (var line in raw)
            {
                var cleaned = line.Replace("{", string.Empty).Replace("}", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                    lines.Add(cleaned);
            }

            return lines;
        }

        private static string NormalizeDispNum(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
                return string.Empty;
            return Regex.Replace(dispNum, "\\s+", string.Empty).ToUpperInvariant();
        }

        private static string GetDispositionPrefix(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
                return string.Empty;

            var match = Regex.Match(dispNum, "^[A-Z]{3}");
            return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        }

        private static string NormalizeOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner))
                return string.Empty;

            var upper = owner.ToUpperInvariant();
            var normalized = Regex.Replace(upper, "[^A-Z0-9]+", string.Empty);
            return normalized;
        }

        private static string MapClientNameForCompare(ExcelLookup lookup, string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return string.Empty;

            var entry = lookup.Lookup(rawName);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Value))
                return entry.Value;

            if (lookup.Values.Count > 0)
            {
                var target = NormalizeOwner(rawName);
                foreach (var value in lookup.Values)
                {
                    if (NormalizeOwner(value) == target)
                        return value;
                }
            }

            return rawName;
        }

        private static bool TryApplyExpiredMarker(Transaction tr, PlsrLabelEntry label, out bool alreadyTagged)
        {
            alreadyTagged = false;
            if (label == null)
                return false;

            var contents = label.RawContents ?? string.Empty;
            if (contents.IndexOf("(Expired)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                alreadyTagged = true;
                return true;
            }

            var delimiter = contents.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) >= 0 ? "\\X" : "\\P";
            var updated = contents + delimiter + "(Expired)";

            if (label.IsDimension)
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is Dimension dimension)
                {
                    dimension.DimensionText = updated;
                    return true;
                }
            }
            else if (label.IsLeader)
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is MLeader mleader)
                {
                    var mt = mleader.MText;
                    mt.Contents = updated;
                    mleader.MText = mt;
                    return true;
                }
            }
            else
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is MText mtext)
                {
                    mtext.Contents = updated;
                    return true;
                }
            }

            return false;
        }

        private static void WritePlsrLog(Database database, string text, Logger logger)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                var docPath = doc?.Name ?? string.Empty;
                var folder = Path.GetDirectoryName(docPath);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
                }

                var logPath = Path.Combine(folder, "PLSR_Check.txt");
                File.WriteAllText(logPath, text);
                logger.WriteLine("PLSR check log written: " + logPath);
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR log write failed: " + ex.Message);
            }
        }

        private static string BuildSectionKeyId(SectionKey key)
        {
            return $"Z{key.Zone}_SEC{NormalizeNumberToken(key.Section)}_TWP{NormalizeNumberToken(key.Township)}_RGE{NormalizeNumberToken(key.Range)}_MER{NormalizeNumberToken(key.Meridian)}";
        }

        private static void PlaceQuarterSectionLabels(
            Database database,
            IEnumerable<QuarterLabelInfo> quarterInfos,
            bool includeLsds,
            Logger logger)
        {
            if (database == null || quarterInfos == null)
                return;

            var uniqueQuarterInfos = new Dictionary<ObjectId, QuarterLabelInfo>();
            foreach (var info in quarterInfos)
            {
                if (info == null || info.QuarterId.IsNull || info.QuarterId.IsErased)
                    continue;

                if (!uniqueQuarterInfos.ContainsKey(info.QuarterId))
                    uniqueQuarterInfos.Add(info.QuarterId, info);
            }

            if (uniqueQuarterInfos.Count == 0)
                return;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                EnsureLayer(database, transaction, "C-SYMBOL");
                EnsureLayer(database, transaction, "C-UNS-T");

                var labelCount = 0;
                var grouped = uniqueQuarterInfos.Values
                    .GroupBy(info => BuildSectionKeyId(info.SectionKey))
                    .ToList();

                foreach (var group in grouped)
                {
                    var sectionInfo = group.FirstOrDefault(g => g != null && !g.SectionPolylineId.IsNull && !g.SectionPolylineId.IsErased);
                    if (sectionInfo == null)
                    {
                        continue;
                    }

                    var sectionPolyline = transaction.GetObject(sectionInfo.SectionPolylineId, OpenMode.ForRead) as Polyline;
                    if (sectionPolyline == null)
                    {
                        continue;
                    }

                    if (!TryGetQuarterAnchors(sectionPolyline, out var sectionAnchors))
                    {
                        sectionAnchors = GetFallbackAnchors(sectionPolyline);
                    }

                    var sectionCenter = new Point2d(
                        0.5 * (sectionAnchors.Top.X + sectionAnchors.Bottom.X),
                        0.5 * (sectionAnchors.Left.Y + sectionAnchors.Right.Y));
                    var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));

                    var barriers = new List<LineSegment2d>();
                    AddPolylineSegments(barriers, sectionPolyline);
                    CollectSectionLineBarriers(transaction, modelSpace, sectionPolyline, includeLsds, barriers);

                    foreach (var info in group)
                    {
                        var quarterPolyline = transaction.GetObject(info.QuarterId, OpenMode.ForRead) as Polyline;
                        if (quarterPolyline == null)
                            continue;

                        var quarterToken = FormatQuarterForSectionLabel(info.Quarter);
                        if (string.IsNullOrWhiteSpace(quarterToken))
                            continue;

                        var sectionDescriptor = BuildSectionDescriptor(info.SectionKey);
                        var normalizedSecType = NormalizeSecType(info.SecType);
                        var isLsec = string.Equals(normalizedSecType, "L-SEC", StringComparison.OrdinalIgnoreCase);

                        var extents = quarterPolyline.GeometricExtents;
                        var quarterCenter = new Point2d(
                            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);

                        var primaryContents = isLsec
                            ? $"{quarterToken} Sec. {sectionDescriptor}"
                            : $"Theor. {quarterToken}\\PSec. {sectionDescriptor}";

                        EstimateMTextFootprint(primaryContents, 20.0, out var labelWidth, out var labelHeight);
                        var requiredClearance = Math.Sqrt((labelWidth * 0.5 * labelWidth * 0.5) + (labelHeight * 0.5 * labelHeight * 0.5)) + 2.0;

                        Point2d labelLocation;
                        bool FitsInQuarter(Point2d p)
                        {
                            if (!IsLabelBoxInsideSection(quarterPolyline, p, labelWidth, labelHeight))
                            {
                                return false;
                            }

                            return GetLineworkClearance(p, barriers) >= requiredClearance;
                        }

                        if (!includeLsds)
                        {
                            if (FitsInQuarter(quarterCenter))
                            {
                                labelLocation = quarterCenter;
                            }
                            else if (TryFindNonOverlapSectionPosition(quarterPolyline, quarterCenter, labelWidth, labelHeight, requiredClearance, barriers, out var openSpot))
                            {
                                labelLocation = openSpot;
                            }
                            else
                            {
                                labelLocation = quarterCenter;
                            }
                        }
                        else
                        {
                            if (TryGetBestQuarterLsdCellCenter(
                                quarterPolyline,
                                eastUnit,
                                northUnit,
                                barriers,
                                FitsInQuarter,
                                out var bestCellCenter,
                                out var bestFitCellCenter))
                            {
                                labelLocation = bestFitCellCenter ?? bestCellCenter;
                            }
                            else if (TryFindNonOverlapSectionPosition(quarterPolyline, quarterCenter, labelWidth, labelHeight, requiredClearance, barriers, out var openSpot))
                            {
                                labelLocation = openSpot;
                            }
                            else
                            {
                                labelLocation = GetLeastCongestedPointInBoundary(quarterPolyline, barriers, quarterCenter);
                            }
                        }

                        var center = new Point3d(labelLocation.X, labelLocation.Y, 0.0);
                        var primary = new MText
                        {
                            Layer = "C-SYMBOL",
                            ColorIndex = 3,
                            TextHeight = 20.0,
                            Location = center,
                            Attachment = AttachmentPoint.MiddleCenter,
                            Contents = primaryContents
                        };
                        modelSpace.AppendEntity(primary);
                        transaction.AddNewlyCreatedDBObject(primary, true);
                        labelCount++;

                        if (!isLsec)
                        {
                            var unsurveyed = new MText
                            {
                                Layer = "C-UNS-T",
                                ColorIndex = 3,
                                TextHeight = 16.0,
                                Location = new Point3d(center.X, center.Y - 34.0, center.Z),
                                Attachment = AttachmentPoint.TopCenter,
                                Contents = "UNSURVEYED\\PTERRITORY"
                            };
                            modelSpace.AppendEntity(unsurveyed);
                            transaction.AddNewlyCreatedDBObject(unsurveyed, true);
                        }
                    }
                }

                transaction.Commit();
                logger?.WriteLine($"Placed {labelCount} quarter section label(s).");
            }
        }

        private static void CollectSectionLineBarriers(
            Transaction transaction,
            BlockTableRecord modelSpace,
            Polyline sectionPolyline,
            bool includeLsds,
            List<LineSegment2d> barriers)
        {
            if (transaction == null || modelSpace == null || sectionPolyline == null || barriers == null)
            {
                return;
            }

            Extents3d sectionExtents;
            try
            {
                sectionExtents = sectionPolyline.GeometricExtents;
            }
            catch
            {
                return;
            }

            foreach (ObjectId id in modelSpace)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity) || entity.IsErased)
                {
                    continue;
                }

                var layer = entity.Layer ?? string.Empty;
                if (string.Equals(layer, "C-SYMBOL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, "C-UNS-T", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Restrict to simple geometry types to avoid unstable extents calls on proxy/custom entities.
                if (!(entity is Line) && !(entity is Polyline))
                {
                    continue;
                }

                try
                {
                    if (entity is Line line)
                    {
                        var lineExtents = line.GeometricExtents;
                        if (!GeometryUtils.ExtentsIntersect(sectionExtents, lineExtents))
                        {
                            continue;
                        }

                        var mid = new Point2d(
                            (line.StartPoint.X + line.EndPoint.X) * 0.5,
                            (line.StartPoint.Y + line.EndPoint.Y) * 0.5);
                        if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, mid))
                        {
                            continue;
                        }

                        barriers.Add(new LineSegment2d(
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y)));
                        continue;
                    }

                    if (entity is Polyline polyline)
                    {
                        var polyExtents = polyline.GeometricExtents;
                        if (!GeometryUtils.ExtentsIntersect(sectionExtents, polyExtents))
                        {
                            continue;
                        }

                        var polyCenter = new Point2d(
                            (polyExtents.MinPoint.X + polyExtents.MaxPoint.X) * 0.5,
                            (polyExtents.MinPoint.Y + polyExtents.MaxPoint.Y) * 0.5);
                        if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, polyCenter))
                        {
                            continue;
                        }

                        AddPolylineSegments(barriers, polyline);
                    }
                }
                catch
                {
                    // Ignore problematic entities during final label barrier collection.
                }
            }
        }

        private static void EstimateMTextFootprint(string contents, double textHeight, out double width, out double height)
        {
            if (textHeight <= 0)
            {
                textHeight = 1.0;
            }

            var normalized = string.IsNullOrWhiteSpace(contents) ? "X" : contents;
            var lines = normalized.Split(new[] { "\\P" }, StringSplitOptions.None);
            var maxChars = Math.Max(1, lines.Max(line => line?.Length ?? 0));
            var lineCount = Math.Max(1, lines.Length);

            width = Math.Max(10.0, maxChars * textHeight * 0.62);
            height = Math.Max(10.0, lineCount * textHeight * 1.25);
        }

        private static string FormatQuarterForSectionLabel(QuarterSelection quarter)
        {
            return quarter switch
            {
                QuarterSelection.NorthWest => "N.W.1/4",
                QuarterSelection.NorthEast => "N.E.1/4",
                QuarterSelection.SouthWest => "S.W.1/4",
                QuarterSelection.SouthEast => "S.E.1/4",
                _ => string.Empty
            };
        }

        private static string BuildSectionDescriptor(SectionKey key)
        {
            var section = NormalizeNumberToken(key.Section);
            var township = NormalizeNumberToken(key.Township);
            var range = NormalizeNumberToken(key.Range);
            var meridian = NormalizeNumberToken(key.Meridian);
            return $"{section}-{township}-{range}-W.{meridian}M.";
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





