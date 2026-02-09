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
        private const bool EnableRoadAllowanceDiagnostics = true;
        private static readonly object SectionOutlineCacheLock = new object();
        private static readonly Dictionary<string, SectionOutline?> SectionOutlineCache = new Dictionary<string, SectionOutline?>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> FolderIndexCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

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
            var supplementalSectionInfos = LoadSupplementalSectionSpatialInfos(input.SectionRequests, config, logger);

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                exitStage = "processing_dispositions";
                var lsdCells = BuildSectionLsdCells(transaction, sectionDrawResult.SectionNumberByPolylineId);
                var sectionInfos = BuildSectionSpatialInfos(transaction, sectionDrawResult.SectionNumberByPolylineId);
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
                    if (IsWellSitePurpose(purpose) || isSurfaceLabel)
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
                    else if ((mappedPurpose ?? string.Empty).IndexOf("(Surface)", StringComparison.OrdinalIgnoreCase) >= 0)
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
            // We always draw the full section outline (ATS fabric on) but may label only specific quarters.
            var labelQuarterIds = new HashSet<ObjectId>();
            var labelQuarterInfos = new List<QuarterLabelInfo>();
            var allQuarterIds = new HashSet<ObjectId>();
            var quarterHelperIds = new HashSet<ObjectId>();
            var sectionLabelIds = new HashSet<ObjectId>();
            var sectionIds = new HashSet<ObjectId>();
            var contextSectionIds = new HashSet<ObjectId>();
            var sectionNumberById = new Dictionary<ObjectId, int>();
            var createdSections = new Dictionary<string, SectionBuildResult>(StringComparer.OrdinalIgnoreCase);
            var createdSectionQuarterSecTypes = new Dictionary<string, Dictionary<QuarterSelection, string>>(StringComparer.OrdinalIgnoreCase);
            var searchFolders = BuildSectionIndexSearchFolders(config);
            var inferredSecTypes = InferSectionTypes(requests, searchFolders, logger);
            var inferredQuarterSecTypes = InferQuarterSectionTypes(requests, searchFolders, logger);

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

            foreach (var id in DrawAdjoiningSectionsForContext(
                database,
                searchFolders,
                requests,
                labelQuarterIds,
                inferredSecTypes,
                inferredQuarterSecTypes,
                logger))
            {
                contextSectionIds.Add(id);
            }

            CleanupContextSectionOverlaps(database, contextSectionIds, logger);

            var generatedRoadAllowanceIds = new HashSet<ObjectId>();
            foreach (var id in DrawRoadAllowanceGapOffsetLines(
                database,
                searchFolders,
                requests,
                labelQuarterIds,
                logger))
            {
                quarterHelperIds.Add(id);
                generatedRoadAllowanceIds.Add(id);
            }

            SnapQuarterLsdLinesToSectionBoundaries(database, labelQuarterIds, logger);
            CleanupGeneratedRoadAllowanceOverlaps(database, generatedRoadAllowanceIds, logger);
            DrawBufferedQuarterWindowsOnDefpoints(database, labelQuarterIds, 100.0, logger);

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
            Logger logger)
        {
            var drawnIds = new List<ObjectId>();
            if (database == null || searchFolders == null || requests == null || requestedQuarterIds == null)
            {
                return drawnIds;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            if (clipWindows.Count == 0)
            {
                return drawnIds;
            }
            var window = UnionExtents3d(clipWindows);

            var requestedSectionKeys = new HashSet<string>(
                requests.Select(r => BuildSectionKeyId(r.Key)),
                StringComparer.OrdinalIgnoreCase);

            var townshipKeys = BuildContextTownshipKeys(requests).ToList();

            var processedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
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

                            if (!GeometryUtils.ExtentsIntersect(sectionPolyline.GeometricExtents, window))
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
                            foreach (var id in DrawSectionBoundaryQuarterSegmentPolylines(
                                modelSpace,
                                tr,
                                nw,
                                ne,
                                sw,
                                se,
                                secType,
                                quarterSecTypes,
                                clipWindows,
                                anchors.Top,
                                anchors.Right,
                                anchors.Bottom,
                                anchors.Left))
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
                logger?.WriteLine($"Context sections drawn: {drawnIds.Count} clipped piece(s) within 100m of requested quarters.");
            }

            return drawnIds;
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

            const double mergeToleranceMeters = 0.01;
            foreach (var window in windows)
            {
                var merged = window;
                var mergedAny = true;
                while (mergedAny)
                {
                    mergedAny = false;
                    for (var i = 0; i < result.Count; i++)
                    {
                        if (!DoExtentsOverlapOrTouch(merged, result[i], mergeToleranceMeters))
                        {
                            continue;
                        }

                        merged.AddExtents(result[i]);
                        result.RemoveAt(i);
                        mergedAny = true;
                        break;
                    }
                }

                result.Add(merged);
            }

            return result;
        }

        private static List<ObjectId> DrawRoadAllowanceGapOffsetLines(
            Database database,
            IReadOnlyList<string> searchFolders,
            IReadOnlyList<SectionRequest> requests,
            IEnumerable<ObjectId> requestedQuarterIds,
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

            var offsetSpecs = new List<(Point2d A, Point2d B, string Tag)>();
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

                        geoms.Add((
                            BuildSectionKeyId(sectionKey),
                            BuildSectionDescriptor(sectionKey),
                            sw, se, nw, ne,
                            anchors.Left, anchors.Right, anchors.Top, anchors.Bottom,
                            center,
                            zone,
                            NormalizeNumberToken(meridian),
                            globalX,
                            globalY));
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
            void AddSpec(Point2d s, Point2d e, string tag, string keyA, string keyB, bool verticalMode)
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

                offsetSpecs.Add((s, e, tag));
                if (EnableRoadAllowanceDiagnostics)
                    logger?.WriteLine($"RA-DIAG ADD {tag}: A=({s.X:0.###},{s.Y:0.###}) B=({e.X:0.###},{e.Y:0.###})");
            }

            void TryAddFromPair(
                string baseKey,
                string baseLabel,
                Point2d baseStart,
                Point2d baseMid,
                Point2d baseEnd,
                string otherKey,
                string otherLabel,
                Point2d otherStart,
                Point2d otherMid,
                Point2d otherEnd,
                bool verticalMode)
            {
                double? gapP1 = null;
                double? gapP2 = null;

                void TryAddQuarterOffsetSegment(Point2d b0, Point2d b1, Point2d o0, Point2d o1, string part, out double? gapOut)
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
                    AddSpec(offsetStart, offsetEnd, tag, baseKey, otherKey, verticalMode);
                }

                TryAddQuarterOffsetSegment(baseStart, baseMid, otherStart, otherMid, "P1", out gapP1);
                TryAddQuarterOffsetSegment(baseMid, baseEnd, otherMid, otherEnd, "P2", out gapP2);

                if (EnableRoadAllowanceDiagnostics)
                {
                    logger?.WriteLine(
                        $"RA-DIAG {(verticalMode ? "V" : "H")} pair {baseLabel}/{otherLabel}: " +
                        $"gapP1={(gapP1.HasValue ? gapP1.Value.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")} " +
                        $"gapP2={(gapP2.HasValue ? gapP2.Value.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")}");
                }
            }

            for (var i = 0; i < geoms.Count; i++)
            {
                var a = geoms[i];
                for (var j = i + 1; j < geoms.Count; j++)
                {
                    var b = geoms[j];
                    if (!selectedOrLocalKeyIds.Contains(a.KeyId) &&
                        !selectedOrLocalKeyIds.Contains(b.KeyId))
                    {
                        continue;
                    }

                    if (a.GlobalX == int.MinValue ||
                        a.GlobalY == int.MinValue ||
                        b.GlobalX == int.MinValue ||
                        b.GlobalY == int.MinValue)
                    {
                        continue;
                    }

                    if (a.Zone != b.Zone ||
                        !string.Equals(a.Meridian, b.Meridian, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var centerDx = b.Center.X - a.Center.X;
                    var centerDy = b.Center.Y - a.Center.Y;
                    var centerDistance = Math.Sqrt((centerDx * centerDx) + (centerDy * centerDy));
                    var spanA = Math.Max((a.SE - a.SW).Length, (a.NW - a.SW).Length);
                    var spanB = Math.Max((b.SE - b.SW).Length, (b.NW - b.SW).Length);
                    if (centerDistance > (Math.Max(spanA, spanB) * 1.8))
                    {
                        continue;
                    }

                    var gridDx = Math.Abs(a.GlobalX - b.GlobalX);
                    var gridDy = Math.Abs(a.GlobalY - b.GlobalY);

                    // Vertical road allowance checks only make sense for immediate east/west neighbors.
                    if (gridDx == 1 && gridDy == 0)
                    {
                        // Use world X for side selection so orientation sign cannot flip west/east.
                        var aIsWest = a.Center.X <= b.Center.X;
                        var west = aIsWest ? a : b;
                        var east = aIsWest ? b : a;
                        // Facing edges only: east edge of west section, west edge of east section.
                        TryAddFromPair(
                            west.KeyId, west.Label, west.SE, west.RightMid, west.NE,
                            east.KeyId, east.Label, east.SW, east.LeftMid, east.NW,
                            verticalMode: true);
                    }

                    // Horizontal road allowance checks only make sense for immediate north/south neighbors.
                    if (gridDx == 0 && gridDy == 1)
                    {
                        // Use world Y for side selection so orientation sign cannot flip south/north.
                        var aIsSouth = a.Center.Y <= b.Center.Y;
                        var south = aIsSouth ? a : b;
                        var north = aIsSouth ? b : a;
                        // Facing edges only: north edge of south section, south edge of north section.
                        TryAddFromPair(
                            south.KeyId, south.Label, south.NW, south.TopMid, south.NE,
                            north.KeyId, north.Label, north.SW, north.BottomMid, north.SE,
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
                EnsureLayer(database, tr, "L-USEC");
                var drawnSegmentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var clippedSegments = new List<(Point2d A, Point2d B, string Tag)>();

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

                        clippedSegments.Add((a, b, seg.Tag));
                        drewAny = true;

                        if (EnableRoadAllowanceDiagnostics)
                            logger?.WriteLine($"RA-DIAG DRAW {seg.Tag}: A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###})");
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

                foreach (var seg in alignedSegments)
                {
                    var poly = new Polyline(2)
                    {
                        Layer = "L-USEC",
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

        private static List<(Point2d A, Point2d B, string Tag)> AlignRoadAllowanceSegmentEndpointsToSectionBoundaries(
            Transaction transaction,
            BlockTableRecord modelSpace,
            IReadOnlyList<Extents3d> clipWindows,
            List<(Point2d A, Point2d B, string Tag)> segments,
            Logger? logger)
        {
            if (transaction == null || modelSpace == null || segments == null || segments.Count == 0)
            {
                return segments ?? new List<(Point2d A, Point2d B, string Tag)>();
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
                const double maxAdjustMeters = 8.0;
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
                    var delta = Math.Abs(bestNeg.Value - tMin);
                    if (delta <= maxAdjustMeters)
                    {
                        newMin = bestNeg.Value;
                        startShift = delta;
                        changed = true;
                    }
                }

                if (bestPos.HasValue)
                {
                    var delta = Math.Abs(bestPos.Value - tMax);
                    if (delta <= maxAdjustMeters)
                    {
                        newMax = bestPos.Value;
                        endShift = delta;
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

            var result = new List<(Point2d A, Point2d B, string Tag)>(segments.Count);
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

                result.Add((a, b, seg.Tag));
            }

            return result;
        }

        private static List<(Point2d A, Point2d B, string Tag)> MergeCollinearRoadAllowanceSegments(List<(Point2d A, Point2d B, string Tag)> input)
        {
            var merged = input == null
                ? new List<(Point2d A, Point2d B, string Tag)>()
                : new List<(Point2d A, Point2d B, string Tag)>(input);
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
                        if (!string.Equals(merged[i].Tag, merged[j].Tag, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryMergeCollinearSegments(merged[i].A, merged[i].B, merged[j].A, merged[j].B, out var outA, out var outB))
                        {
                            continue;
                        }

                        merged[i] = (outA, outB, merged[i].Tag);
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
            var generatedSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
            var existingSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();

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
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (generatedSet.Contains(id))
                    {
                        generatedSegments.Add((id, a, b));
                    }
                    else
                    {
                        existingSegments.Add((id, a, b));
                    }
                }

                var toErase = new HashSet<ObjectId>();
                foreach (var g in generatedSegments)
                {
                    foreach (var e in existingSegments)
                    {
                        if (!AreSegmentsDuplicateOrCollinearOverlap(g.A, g.B, e.A, e.B))
                        {
                            continue;
                        }

                        toErase.Add(g.Id);
                        break;
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
                    logger?.WriteLine($"Cleanup: erased {erased} generated RA segment(s) overlapping existing section linework.");
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
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
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

        private static bool AreSegmentsDuplicateOrCollinearOverlap(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
        {
            const double endpointTol = 0.05;
            const double angleTol = 0.001;
            const double distanceTol = 0.10;
            const double overlapTol = 0.05;

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

            var segments = new[]
            {
                (A: sw, B: sMid, Quarter: QuarterSelection.SouthWest),
                (A: sMid, B: se, Quarter: QuarterSelection.SouthEast),
                (A: se, B: eMid, Quarter: QuarterSelection.SouthEast),
                (A: eMid, B: ne, Quarter: QuarterSelection.NorthEast),
                (A: ne, B: nMid, Quarter: QuarterSelection.NorthEast),
                (A: nMid, B: nw, Quarter: QuarterSelection.NorthWest),
                (A: nw, B: wMid, Quarter: QuarterSelection.NorthWest),
                (A: wMid, B: sw, Quarter: QuarterSelection.SouthWest),
            };

            foreach (var seg in segments)
            {
                if (!TryClipSegmentToWindow(seg.A, seg.B, clipToWindow, out var a, out var b))
                {
                    continue;
                }

                var poly = new Polyline(2)
                {
                    Layer = ResolveQuarterSecTypeForQuarter(quarterSecTypes, seg.Quarter, secType),
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
            var segments = new[]
            {
                (A: sw, B: sMid, Quarter: QuarterSelection.SouthWest),
                (A: sMid, B: se, Quarter: QuarterSelection.SouthEast),
                (A: se, B: eMid, Quarter: QuarterSelection.SouthEast),
                (A: eMid, B: ne, Quarter: QuarterSelection.NorthEast),
                (A: ne, B: nMid, Quarter: QuarterSelection.NorthEast),
                (A: nMid, B: nw, Quarter: QuarterSelection.NorthWest),
                (A: nw, B: wMid, Quarter: QuarterSelection.NorthWest),
                (A: wMid, B: sw, Quarter: QuarterSelection.SouthWest),
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

                    var key = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                        a.X, a.Y, b.X, b.Y);
                    if (!dedupe.Add(key))
                    {
                        continue;
                    }

                    var poly = new Polyline(2)
                    {
                        Layer = ResolveQuarterSecTypeForQuarter(quarterSecTypes, seg.Quarter, secType),
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
            Editor editor,
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
            foreach (DBObject dbObject in exploded)
            {
                if (dbObject is Entity entity)
                {
                    modelSpace.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                }
                else
                {
                    dbObject.Dispose();
                }
            }

            blockRef.Erase();
            editor?.WriteMessage($"\nLSD label inserted/exploded: {preferredName} ({quarter}).");
        }

        private static bool TryEnsureLsdBlockLoaded(Database database, Transaction transaction, string blockName, Editor editor)
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

            for (var i = 0; i < geoms.Count; i++)
            {
                var a = geoms[i];
                for (var j = i + 1; j < geoms.Count; j++)
                {
                    var b = geoms[j];
                    if (!selectedOrLocalKeyIds.Contains(a.KeyId) &&
                        !selectedOrLocalKeyIds.Contains(b.KeyId))
                    {
                        continue;
                    }

                    if (a.GlobalX == int.MinValue ||
                        a.GlobalY == int.MinValue ||
                        b.GlobalX == int.MinValue ||
                        b.GlobalY == int.MinValue)
                    {
                        continue;
                    }

                    if (a.Zone != b.Zone ||
                        !string.Equals(a.Meridian, b.Meridian, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var centerDx = b.Center.X - a.Center.X;
                    var centerDy = b.Center.Y - a.Center.Y;
                    var centerDistance = Math.Sqrt((centerDx * centerDx) + (centerDy * centerDy));
                    var spanA = Math.Max((a.SE - a.SW).Length, (a.NW - a.SW).Length);
                    var spanB = Math.Max((b.SE - b.SW).Length, (b.NW - b.SW).Length);
                    if (centerDistance > (Math.Max(spanA, spanB) * 1.8))
                    {
                        continue;
                    }

                    var gridDx = Math.Abs(a.GlobalX - b.GlobalX);
                    var gridDy = Math.Abs(a.GlobalY - b.GlobalY);

                    if (gridDx == 1 && gridDy == 0)
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

                    if (gridDx == 0 && gridDy == 1)
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

                    logger?.WriteLine(
                        $"SEC TYPE inferred: {keyId} -> {inferred} (eastGap={(haveEastGap ? eastGap.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")}, southGap={(haveSouthGap ? southGap.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")})");
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
                ClearPreviousBufferedDefpointsWindows(tr, ms);

                var count = 0;
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
                    ms.AppendEntity(boundary);
                    tr.AddNewlyCreatedDBObject(boundary, true);
                    count++;
                }

                tr.Commit();
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

        private static void ClearPreviousBufferedDefpointsWindows(Transaction tr, BlockTableRecord modelSpace)
        {
            if (tr == null || modelSpace == null)
            {
                return;
            }

            foreach (ObjectId id in modelSpace)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead, false) is Polyline pl))
                {
                    continue;
                }

                if (!pl.Closed)
                {
                    continue;
                }

                if (!string.Equals(pl.Layer, "DEFPOINTS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (pl.ColorIndex != 8)
                {
                    continue;
                }

                pl.UpgradeOpen();
                pl.Erase();
            }
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

                for (var dRange = -1; dRange <= 1; dRange++)
                {
                    for (var dTownship = -1; dTownship <= 1; dTownship++)
                    {
                        var candidateRange = rangeNum + dRange;
                        var candidateTownship = townshipNum + dTownship;
                        if (candidateRange <= 0 || candidateTownship <= 0)
                        {
                            continue;
                        }

                        var key = new SectionKey(
                            request.Key.Zone,
                            "1",
                            candidateTownship.ToString(CultureInfo.InvariantCulture),
                            candidateRange.ToString(CultureInfo.InvariantCulture),
                            request.Key.Meridian);
                        keys.Add(BuildTownshipKey(key));
                    }
                }
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


