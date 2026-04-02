using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ImportExport;
using AtsBackgroundBuilder.Dispositions;
using AtsBackgroundBuilder.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static readonly string[] DispositionShapeUpdateSourceRoots =
        {
            @"N:\Mapping\FTP Updates\AltaLIS",
            @"O:\Mapping\FTP Updates\AltaLIS",
        };

        private static readonly string[] CompassMappingShapeUpdateSourceRoots =
        {
            @"N:\Mapping\Mapping\COMPASS_SURVEYED\SHP",
            @"O:\Mapping\Mapping\COMPASS_SURVEYED\SHP",
        };

        private static readonly string[] CrownReservationsShapeUpdateSourceRoots =
        {
            @"N:\Mapping\FTP Updates\GoA",
            @"O:\Mapping\FTP Updates\GoA",
        };

        private static readonly string[] CompassMappingShapeBaseNames =
        {
            "SURVEYED_POLYGON_N83UTMZ11",
            "SURVEYED_POLYGON_N83UTMZ12",
        };

        private static readonly string[] CrownReservationsShapeBaseNames =
        {
            "CrownLandReservations",
        };

        private const string P3ImportRegAppName = "ATSBUILD_P3";
        private const string P3ImportMarker = "IMPORTED";

        private const string DispositionShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\DISPOS";
        private const string CompassMappingShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\COMPASS MAPPING";
        private const string CrownReservationsShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\CLR";

        private static readonly HashSet<string> ShapeUpdateTrackedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".shp",
            ".shx",
            ".dbf",
            ".prj",
            ".cpg",
        };

        private static void AutoUpdateSelectedShapeSetsIfNeeded(AtsBuildInput input, Config config, Logger? logger)
        {
            if (input == null)
            {
                return;
            }

            var overallTimer = Stopwatch.StartNew();
            try
            {
                var unavailableUpdateMessages = new List<string>();
                var needsDisposition = input.IncludeDispositionLinework || input.IncludeDispositionLabels;
                if (needsDisposition)
                {
                    AutoUpdateDispositionShapesIfNeeded(config, logger, unavailableUpdateMessages);
                }

                if (input.IncludeCompassMapping)
                {
                    var compassTimer = Stopwatch.StartNew();
                    var compassError = string.Empty;

                    var compassResolveTimer = Stopwatch.StartNew();
                    var hasCompassSource = TryResolveFirstExistingRootAcrossRoots(
                        CompassMappingShapeUpdateSourceRoots,
                        out var compassSource,
                        out compassError);
                    compassResolveTimer.Stop();
                    logger?.WriteLine($"Shape auto-update timing [Compass Mapping]: resolve source took {compassResolveTimer.ElapsedMilliseconds} ms.");

                    if (hasCompassSource)
                    {
                        var compassDiffTimer = Stopwatch.StartNew();
                        var compassNeedsUpdate = DirectoryContentsDifferForBaseNames(compassSource, CompassMappingShapeDestinationFolder, CompassMappingShapeBaseNames);
                        compassDiffTimer.Stop();
                        logger?.WriteLine($"Shape auto-update timing [Compass Mapping]: compare source/destination took {compassDiffTimer.ElapsedMilliseconds} ms.");

                        if (compassNeedsUpdate)
                        {
                            var compassPromptTimer = Stopwatch.StartNew();
                            var shouldUpdateCompass = ConfirmShapeUpdateIfNewer("Compass Mapping", compassSource);
                            compassPromptTimer.Stop();
                            logger?.WriteLine($"Shape auto-update timing [Compass Mapping]: user prompt wait took {compassPromptTimer.ElapsedMilliseconds} ms.");

                            if (shouldUpdateCompass)
                            {
                                var compassCopyTimer = Stopwatch.StartNew();
                                var copied = ReplaceDirectoryContentsWithSelectedShapeSets(
                                    compassSource,
                                    CompassMappingShapeDestinationFolder,
                                    CompassMappingShapeBaseNames);
                                compassCopyTimer.Stop();
                                logger?.WriteLine($"Shape auto-update: Compass Mapping copied {copied} file(s) from '{compassSource}'.");
                                logger?.WriteLine($"Shape auto-update timing [Compass Mapping]: copy took {compassCopyTimer.ElapsedMilliseconds} ms.");
                            }
                            else
                            {
                                logger?.WriteLine($"Shape auto-update: Compass Mapping update declined by user (source '{compassSource}').");
                            }
                        }
                        else
                        {
                            logger?.WriteLine("Shape auto-update: Compass Mapping already current.");
                        }
                    }
                    else
                    {
                        logger?.WriteLine("Shape auto-update: Compass Mapping skipped. " + compassError);
                        unavailableUpdateMessages.Add(BuildUpdateUnavailableMessage("Compass Mapping", compassError));
                    }

                    compassTimer.Stop();
                    logger?.WriteLine($"Shape auto-update timing [Compass Mapping]: total took {compassTimer.ElapsedMilliseconds} ms.");
                }

                if (input.IncludeCrownReservations)
                {
                    var crownTimer = Stopwatch.StartNew();
                    var crownError = string.Empty;

                    var crownResolveTimer = Stopwatch.StartNew();
                    var hasCrownSource = TryResolveNewestDatedFolderAcrossRoots(
                        CrownReservationsShapeUpdateSourceRoots,
                        out var crownSourceRoot,
                        out var crownSourceFolder,
                        out var crownSourceDate,
                        out crownError);
                    crownResolveTimer.Stop();
                    logger?.WriteLine($"Shape auto-update timing [Crown Reservations]: resolve source took {crownResolveTimer.ElapsedMilliseconds} ms.");

                    if (hasCrownSource)
                    {
                        var crownDiffTimer = Stopwatch.StartNew();
                        var crownNeedsUpdate = DirectoryContentsDifferForBaseNames(crownSourceFolder, CrownReservationsShapeDestinationFolder, CrownReservationsShapeBaseNames);
                        crownDiffTimer.Stop();
                        logger?.WriteLine($"Shape auto-update timing [Crown Reservations]: compare source/destination took {crownDiffTimer.ElapsedMilliseconds} ms.");

                        if (crownNeedsUpdate)
                        {
                            var crownPromptTimer = Stopwatch.StartNew();
                            var shouldUpdateCrown = ConfirmShapeUpdateIfNewer("Crown Reservations", crownSourceFolder);
                            crownPromptTimer.Stop();
                            logger?.WriteLine($"Shape auto-update timing [Crown Reservations]: user prompt wait took {crownPromptTimer.ElapsedMilliseconds} ms.");

                            if (shouldUpdateCrown)
                            {
                                var crownCopyTimer = Stopwatch.StartNew();
                                var copied = ReplaceDirectoryContentsWithSelectedShapeSets(
                                    crownSourceFolder,
                                    CrownReservationsShapeDestinationFolder,
                                    CrownReservationsShapeBaseNames);
                                crownCopyTimer.Stop();
                                logger?.WriteLine($"Shape auto-update: Crown Reservations copied {copied} file(s) from '{crownSourceFolder}' (root '{crownSourceRoot}', date {crownSourceDate:yyyy-MM-dd}).");
                                logger?.WriteLine($"Shape auto-update timing [Crown Reservations]: copy took {crownCopyTimer.ElapsedMilliseconds} ms.");
                            }
                            else
                            {
                                logger?.WriteLine($"Shape auto-update: Crown Reservations update declined by user (source '{crownSourceFolder}').");
                            }
                        }
                        else
                        {
                            logger?.WriteLine("Shape auto-update: Crown Reservations already current.");
                        }
                    }
                    else
                    {
                        logger?.WriteLine("Shape auto-update: Crown Reservations skipped. " + crownError);
                        unavailableUpdateMessages.Add(BuildUpdateUnavailableMessage("Crown Reservations", crownError));
                    }

                    crownTimer.Stop();
                    logger?.WriteLine($"Shape auto-update timing [Crown Reservations]: total took {crownTimer.ElapsedMilliseconds} ms.");
                }

                if (unavailableUpdateMessages.Count > 0)
                {
                    ShowShapeUpdateUnavailableWarning(unavailableUpdateMessages);
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("Shape auto-update failed: " + ex.Message);
                ShowShapeUpdateUnavailableWarning(new[]
                {
                    "Unable to update requested shape files before build.",
                    ex.Message,
                });
            }
            finally
            {
                overallTimer.Stop();
                logger?.WriteLine($"Shape auto-update timing: total pre-build check took {overallTimer.ElapsedMilliseconds} ms.");
            }
        }

        private static void AutoUpdateDispositionShapesIfNeeded(
            Config? config,
            Logger? logger,
            IList<string>? unavailableUpdateMessages = null)
        {
            var dispositionTimer = Stopwatch.StartNew();
            var dispositionError = string.Empty;
            var dispositionShapeBaseNames = BuildShapeBaseNamesFromShapefileNames(config?.DispositionShapefiles, "DAB_APPL");

            var dispositionResolveTimer = Stopwatch.StartNew();
            var hasDispositionSource = TryResolveNewestDidsFolderAcrossRoots(
                DispositionShapeUpdateSourceRoots,
                out var sourceRoot,
                out var newestFolder,
                out var newestDate,
                out dispositionError);
            dispositionResolveTimer.Stop();
            logger?.WriteLine($"Shape auto-update timing [Dispositions]: resolve source took {dispositionResolveTimer.ElapsedMilliseconds} ms.");

            if (hasDispositionSource)
            {
                var dispositionDiffTimer = Stopwatch.StartNew();
                var dispositionNeedsUpdate = DirectoryContentsDifferForBaseNames(newestFolder, DispositionShapeDestinationFolder, dispositionShapeBaseNames);
                dispositionDiffTimer.Stop();
                logger?.WriteLine($"Shape auto-update timing [Dispositions]: compare source/destination took {dispositionDiffTimer.ElapsedMilliseconds} ms.");

                if (dispositionNeedsUpdate)
                {
                    var dispositionPromptTimer = Stopwatch.StartNew();
                    var shouldUpdateDisposition = ConfirmShapeUpdateIfNewer("Dispositions", newestFolder);
                    dispositionPromptTimer.Stop();
                    logger?.WriteLine($"Shape auto-update timing [Dispositions]: user prompt wait took {dispositionPromptTimer.ElapsedMilliseconds} ms.");

                    if (shouldUpdateDisposition)
                    {
                        var dispositionCopyTimer = Stopwatch.StartNew();
                        var copied = ReplaceDirectoryContentsWithSelectedShapeSets(
                            newestFolder,
                            DispositionShapeDestinationFolder,
                            dispositionShapeBaseNames);
                        dispositionCopyTimer.Stop();
                        logger?.WriteLine($"Shape auto-update: Disposition copied {copied} file(s) from '{newestFolder}' (root '{sourceRoot}', date {newestDate:yyyy-MM-dd}, shapeSets={string.Join(", ", dispositionShapeBaseNames)}).");
                        logger?.WriteLine($"Shape auto-update timing [Dispositions]: copy took {dispositionCopyTimer.ElapsedMilliseconds} ms.");
                    }
                    else
                    {
                        logger?.WriteLine($"Shape auto-update: Disposition update declined by user (source '{newestFolder}').");
                    }
                }
                else
                {
                    logger?.WriteLine("Shape auto-update: Disposition already current.");
                }
            }
            else
            {
                logger?.WriteLine("Shape auto-update: Disposition skipped. " + dispositionError);
                var unavailableMessage = BuildUpdateUnavailableMessage("Dispositions", dispositionError);
                if (unavailableUpdateMessages != null)
                {
                    unavailableUpdateMessages.Add(unavailableMessage);
                }
                else
                {
                    ShowShapeUpdateUnavailableWarning(new[] { unavailableMessage });
                }
            }

            dispositionTimer.Stop();
            logger?.WriteLine($"Shape auto-update timing [Dispositions]: total took {dispositionTimer.ElapsedMilliseconds} ms.");
        }

        private static bool IsTrackedShapeUpdateExtension(string pathOrExtension)
        {
            if (string.IsNullOrWhiteSpace(pathOrExtension))
            {
                return false;
            }

            var extension = pathOrExtension;
            if (!extension.StartsWith(".", StringComparison.Ordinal))
            {
                extension = Path.GetExtension(pathOrExtension) ?? string.Empty;
            }

            return ShapeUpdateTrackedExtensions.Contains(extension);
        }

        private static bool ConfirmShapeUpdateIfNewer(string shapeLabel, string sourcePath)
        {
            var label = string.IsNullOrWhiteSpace(shapeLabel) ? "requested shape files" : shapeLabel.Trim();
            var displayPath = string.IsNullOrWhiteSpace(sourcePath) ? "(unknown source path)" : sourcePath.Trim();
            var message =
                $"There are newer Shapes for {label} Located at:\n{displayPath}\n\nWould you like to update them?";
            var decision = System.Windows.Forms.MessageBox.Show(
                message,
                "ATSBUILD",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Question,
                System.Windows.Forms.MessageBoxDefaultButton.Button1);
            return decision == System.Windows.Forms.DialogResult.Yes;
        }

        private static string BuildUpdateUnavailableMessage(string shapeLabel, string details)
        {
            var label = string.IsNullOrWhiteSpace(shapeLabel) ? "Requested shape files" : shapeLabel.Trim();
            var normalizedDetails = string.IsNullOrWhiteSpace(details)
                ? "No additional details were provided."
                : details.Trim();
            return
                $"{label}: unable to update because the source drive or update folder could not be reached.\n{normalizedDetails}";
        }

        private static void ShowShapeUpdateUnavailableWarning(IEnumerable<string> messages)
        {
            if (messages == null)
            {
                return;
            }

            var filtered = messages
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();
            if (filtered.Count == 0)
            {
                return;
            }

            var combined = string.Join("\n\n", filtered);
            System.Windows.Forms.MessageBox.Show(
                "One or more requested shape updates were unable to run before build.\n\n" + combined,
                "ATSBUILD",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
        private static P3ImportSummary ImportP3Shapefiles(
            Database database,
            Editor editor,
            Logger logger,
            IReadOnlyList<ObjectId> scopePolylineIds)
        {
            const string p3Folder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\P3";
            const string outputLayer = "T-WATER-P3";
            const double scopeBuffer = 100.0;
            var shapefiles = new[]
            {
                "BF_Hydro_Polygon.shp",
                "BF_SLNET_arc.shp"
            };

            var summary = new P3ImportSummary();
            var scopeExtents = BuildScopeExtents(database, scopePolylineIds, scopeBuffer);
            if (scopeExtents.Count == 0)
            {
                logger?.WriteLine("P3 import skipped: no requested work area extents.");
                return summary;
            }

            var scopeWindows = BuildScopeWindows(scopeExtents);
            ClearLayerEntities(
                database,
                outputLayer,
                logger,
                scopeExtents,
                requiredXDataAppName: P3ImportRegAppName,
                requiredXDataString: P3ImportMarker);

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

                logger?.WriteLine($"P3 import file start: file={fileName}, path={path}.");
                var preScanTimer = Stopwatch.StartNew();
                var beforeIds = CaptureModelSpaceEntityIds(database);
                preScanTimer.Stop();
                logger?.WriteLine($"P3 import pre-scan complete: file={fileName}, modelSpaceCount={beforeIds.Count}, elapsedMs={preScanTimer.ElapsedMilliseconds}.");
                try
                {
                    importer.Init("SHP", path);
                    logger?.WriteLine($"P3 importer init complete: file={fileName}.");
                    TrySetImporterLocationWindow(
                        importer,
                        new List<Extents2d>(scopeExtents),
                        logger);
                    var enabledInputLayers = 0;
                    foreach (InputLayer layer in importer)
                    {
                        layer.ImportFromInputLayerOn = true;
                        enabledInputLayers++;
                    }
                    logger?.WriteLine($"P3 importer layer enable complete: file={fileName}, inputLayers={enabledInputLayers}.");

                    var rawImportTimer = Stopwatch.StartNew();
                    importer.Import();
                    rawImportTimer.Stop();
                    logger?.WriteLine($"P3 importer import complete: file={fileName}, elapsedMs={rawImportTimer.ElapsedMilliseconds}.");
                }
                catch (System.Exception ex)
                {
                    logger?.WriteLine("P3 import failed for " + path + ": " + ex.Message);
                    summary.ImportFailures++;
                    continue;
                }

                var postImportScanTimer = Stopwatch.StartNew();
                var afterIds = CaptureModelSpaceEntityIds(database);
                postImportScanTimer.Stop();
                var newIds = afterIds.Where(id => !beforeIds.Contains(id)).ToList();
                logger?.WriteLine($"P3 import post-scan complete: file={fileName}, newIds={newIds.Count}, elapsedMs={postImportScanTimer.ElapsedMilliseconds}.");
                var convertedPolygonCount = 0;
                var unconvertedPolygonCount = 0;
                var fileImportedCount = 0;
                var fileFilteredCount = 0;
                var postProcessTimer = Stopwatch.StartNew();

                using (var tr = database.TransactionManager.StartTransaction())
                {
                    EnsureLayer(database, tr, outputLayer);
                    var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    foreach (var id in newIds)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (ent == null || ent.IsErased)
                        {
                            continue;
                        }

                        var overlapKind = ClassifyEntityScopeOverlap(
                            ent,
                            scopeExtents);
                        if (overlapKind == ScopeOverlapKind.None)
                        {
                            ent.Erase(true);
                            summary.FilteredEntities++;
                            fileFilteredCount++;
                            continue;
                        }

                        if (IsPolygonLikeEntity(ent))
                        {
                            var converted = TryConvertPolygonEntityToPolyline(ent, tr, modelSpace, logger);
                            if (converted != null)
                            {
                                ent = converted;
                                convertedPolygonCount++;
                                overlapKind = ClassifyEntityScopeOverlap(ent, scopeExtents);
                            }
                            else
                            {
                                unconvertedPolygonCount++;
                            }
                        }

                        if (overlapKind == ScopeOverlapKind.Partial)
                        {
                            var keepWhole = ShouldKeepWholePartialP3Entity(ent, scopeWindows);
                            if (!keepWhole)
                            {
                                ent.Erase(true);
                                summary.FilteredEntities++;
                                fileFilteredCount++;
                                continue;
                            }
                        }

                        MarkEntityWithImportTag(database, tr, ent, P3ImportRegAppName, P3ImportMarker);
                        ent.Layer = outputLayer;
                        ent.ColorIndex = 256;
                        summary.ImportedEntities++;
                        fileImportedCount++;
                    }

                    tr.Commit();
                }
                postProcessTimer.Stop();

                logger?.WriteLine(
                    $"P3 import post-process complete: file={fileName}, kept={fileImportedCount}, filtered={fileFilteredCount}, converted={convertedPolygonCount}, unconverted={unconvertedPolygonCount}, elapsedMs={postProcessTimer.ElapsedMilliseconds}.");

                if (convertedPolygonCount > 0 || unconvertedPolygonCount > 0)
                {
                    logger?.WriteLine(
                        $"P3 polygon conversion: converted={convertedPolygonCount}, unconverted={unconvertedPolygonCount}, file={fileName}.");
                }
            }

            editor?.WriteMessage($"\nImported {summary.ImportedEntities} P3 entities.");
            return summary;
        }

        private static P3ImportSummary ImportCompassMappingShapefile(
            Database database,
            Editor editor,
            Logger logger,
            IReadOnlyList<ObjectId> scopePolylineIds,
            int zone)
        {
            const string compassFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\COMPASS MAPPING";
            const string outputLayer = "T-COMPASS-MAPPING";
            var fileName = zone == 12
                ? "SURVEYED_POLYGON_N83UTMZ12.shp"
                : "SURVEYED_POLYGON_N83UTMZ11.shp";

            var summary = new P3ImportSummary();
            ClearLayerEntities(database, outputLayer, logger);
            var importConfig = new Config
            {
                ShapefileFolder = compassFolder,
                DispositionShapefileFolder = compassFolder,
                DispositionShapefiles = new[] { fileName }
            };
            var imported = new List<ObjectId>();
            var importSummary = ShapefileImporter.ImportShapefiles(
                database,
                editor,
                logger,
                importConfig,
                scopePolylineIds,
                imported,
                scopeBufferMeters: 100.0,
                utmZoneHint: zone);
            summary.FilteredEntities = importSummary.FilteredDispositions;
            summary.ImportFailures = importSummary.ImportFailures;
            if (imported.Count == 0)
            {
                return summary;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tr, outputLayer);

                foreach (var id in imported.Distinct())
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Layer = outputLayer;
                    ent.ColorIndex = 256;
                    summary.ImportedEntities++;
                }

                tr.Commit();
            }

            editor?.WriteMessage($"\nImported {summary.ImportedEntities} Compass Mapping entities.");
            return summary;
        }

        private static P3ImportSummary ImportCrownReservationsShapefile(
            Database database,
            Editor editor,
            Logger logger,
            IReadOnlyList<ObjectId> scopePolylineIds)
        {
            const string crownFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\CLR";
            const string outputLayer = "T-CROWN-RESERVATIONS";
            const string fileName = "CrownLandReservations.shp";

            var summary = new P3ImportSummary();
            ClearLayerEntities(database, outputLayer, logger);
            var importConfig = new Config
            {
                ShapefileFolder = crownFolder,
                DispositionShapefileFolder = crownFolder,
                DispositionShapefiles = new[] { fileName }
            };
            var imported = new List<ObjectId>();
            var importSummary = ShapefileImporter.ImportShapefiles(
                database,
                editor,
                logger,
                importConfig,
                scopePolylineIds,
                imported,
                scopeBufferMeters: 100.0);
            summary.FilteredEntities = importSummary.FilteredDispositions;
            summary.ImportFailures = importSummary.ImportFailures;
            if (imported.Count == 0)
            {
                return summary;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tr, outputLayer);

                foreach (var id in imported.Distinct())
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    ent.Layer = outputLayer;
                    ent.ColorIndex = 256;
                    summary.ImportedEntities++;
                }

                tr.Commit();
            }

            editor?.WriteMessage($"\nImported {summary.ImportedEntities} Crown Reservations entities.");
            return summary;
        }

        private static void ClearLayerEntities(
            Database database,
            string layerName,
            Logger? logger,
            IReadOnlyList<Extents2d>? scopeExtents = null,
            string? requiredXDataAppName = null,
            string? requiredXDataString = null)
        {
            if (database == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

            var scopeWindows = BuildScopeWindows(scopeExtents);
            var hasScopeExtents = scopeExtents is { Count: > 0 };
            var erased = 0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (ObjectId id in modelSpace)
                {
                    var ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var overlapKind = ScopeOverlapKind.FullyInside;
                    if (hasScopeExtents)
                    {
                        overlapKind = ClassifyEntityScopeOverlap(ent, scopeExtents);
                        if (overlapKind == ScopeOverlapKind.None)
                        {
                            continue;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(requiredXDataAppName) &&
                        !EntityHasImportTag(ent, requiredXDataAppName, requiredXDataString))
                    {
                        continue;
                    }

                    if (overlapKind == ScopeOverlapKind.Partial &&
                        scopeWindows.Count > 0 &&
                        string.Equals(layerName, "T-WATER-P3", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(requiredXDataAppName, P3ImportRegAppName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(requiredXDataString, P3ImportMarker, StringComparison.OrdinalIgnoreCase) &&
                        !ShouldKeepWholePartialP3Entity(ent, scopeWindows))
                    {
                        continue;
                    }

                    try
                    {
                        ent.Erase(true);
                        erased++;
                    }
                    catch
                    {
                        // ignore per-entity failures
                    }
                }

                tr.Commit();
            }

            if (erased > 0)
            {
                if (hasScopeExtents)
                {
                    logger?.WriteLine($"Cleared {erased} existing tagged entity/ies from layer '{layerName}' inside requested import scope before import.");
                }
                else
                {
                    logger?.WriteLine($"Cleared {erased} existing entity/ies from layer '{layerName}' before import.");
                }
            }

        }

        private static bool EntityHasImportTag(Entity entity, string regAppName, string? marker)
        {
            if (entity == null || string.IsNullOrWhiteSpace(regAppName))
            {
                return false;
            }

            var buffer = entity.XData;
            if (buffer == null)
            {
                return false;
            }

            using (buffer)
            {
                var inTargetApp = false;
                foreach (var value in buffer.AsArray())
                {
                    if (value.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                    {
                        inTargetApp = string.Equals(value.Value as string, regAppName, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inTargetApp)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(marker))
                    {
                        return true;
                    }

                    if (value.TypeCode == (int)DxfCode.ExtendedDataAsciiString &&
                        string.Equals(value.Value as string, marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void MarkEntityWithImportTag(
            Database database,
            Transaction transaction,
            Entity entity,
            string regAppName,
            string marker)
        {
            if (database == null ||
                transaction == null ||
                entity == null ||
                string.IsNullOrWhiteSpace(regAppName) ||
                EntityHasImportTag(entity, regAppName, marker))
            {
                return;
            }

            EnsureRegApp(database, transaction, regAppName);
            var values = new List<TypedValue>();
            var existing = entity.XData;
            if (existing != null)
            {
                using (existing)
                {
                    values.AddRange(existing.AsArray());
                }
            }

            values.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, regAppName));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, marker));
            entity.XData = new ResultBuffer(values.ToArray());
        }

        private static void EnsureRegApp(Database database, Transaction transaction, string regAppName)
        {
            if (database == null || transaction == null || string.IsNullOrWhiteSpace(regAppName))
            {
                return;
            }

            var regAppTable = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (regAppTable.Has(regAppName))
            {
                return;
            }

            regAppTable.UpgradeOpen();
            using (var record = new RegAppTableRecord())
            {
                record.Name = regAppName;
                regAppTable.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
        }

        private static string BuildCompassOdTableName(string shapefilePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(shapefilePath) ?? "COMPASS_MAPPING";
            var sanitized = new string(baseName.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "COMPASS_MAPPING";
            }

            if (char.IsDigit(sanitized[0]))
            {
                sanitized = "ATS_" + sanitized;
            }

            if (!sanitized.StartsWith("ATS_", StringComparison.OrdinalIgnoreCase))
            {
                sanitized = "ATS_" + sanitized;
            }

            const int maxLen = 31;
            return sanitized.Length <= maxLen ? sanitized : sanitized.Substring(0, maxLen);
        }

        private static ImportDataMapping DetermineCompassDataMappingMode(string odTableName, Logger? logger)
        {
            try
            {
                var tables = HostMapApplicationServices.Application?.ActiveProject?.ODTables;
                if (tables != null)
                {
                    var names = tables.GetTableNames();
                    if (names != null)
                    {
                        foreach (var nObj in names)
                        {
                            var n = nObj as string ?? nObj?.ToString();
                            if (string.Equals(n, odTableName, StringComparison.OrdinalIgnoreCase))
                            {
                                return ImportDataMapping.ExistingObjectDataOnly;
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("Compass Mapping OD table lookup failed: " + ex.Message);
            }

            return ImportDataMapping.NewObjectDataOnly;
        }

        private static bool TryResolveFirstExistingRootAcrossRoots(
            IReadOnlyList<string> roots,
            out string selectedRoot,
            out string error)
        {
            selectedRoot = string.Empty;
            error = string.Empty;
            if (roots == null || roots.Count == 0)
            {
                error = "No candidate roots configured.";
                return false;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    selectedRoot = root;
                    return true;
                }
            }

            error = "None of the candidate roots exist:\n" + string.Join("\n", roots);
            return false;
        }

        private static bool TryResolveNewestDidsFolderAcrossRoots(
            IReadOnlyList<string> roots,
            out string selectedSourceRoot,
            out string newestFolder,
            out DateTime newestDate,
            out string error)
        {
            selectedSourceRoot = string.Empty;
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;

            var existingRoots = roots.Where(Directory.Exists).ToList();
            if (existingRoots.Count == 0)
            {
                error = "Unable to find AltaLIS FTP update folder.\nChecked:\n" + string.Join("\n", roots);
                return false;
            }

            var foundAny = false;
            var bestDate = DateTime.MinValue;
            var bestFolder = string.Empty;
            var bestRoot = string.Empty;
            var diagnostics = new List<string>();
            foreach (var root in existingRoots)
            {
                if (!TryFindNewestDidsFolder(root, out var candidateFolder, out var candidateDate, out var rootError))
                {
                    diagnostics.Add(rootError);
                    continue;
                }

                if (!foundAny || candidateDate > bestDate)
                {
                    foundAny = true;
                    bestDate = candidateDate;
                    bestFolder = candidateFolder;
                    bestRoot = root;
                }
            }

            if (!foundAny)
            {
                error = "No dated dids_* folders were found in available AltaLIS roots.\n" + string.Join("\n", diagnostics);
                return false;
            }

            selectedSourceRoot = bestRoot;
            newestFolder = bestFolder;
            newestDate = bestDate;
            return true;
        }

        private static bool TryResolveNewestDatedFolderAcrossRoots(
            IReadOnlyList<string> roots,
            out string selectedSourceRoot,
            out string newestFolder,
            out DateTime newestDate,
            out string error)
        {
            selectedSourceRoot = string.Empty;
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;

            var existingRoots = roots.Where(Directory.Exists).ToList();
            if (existingRoots.Count == 0)
            {
                error = "Unable to find update folder root.\nChecked:\n" + string.Join("\n", roots);
                return false;
            }

            var foundAny = false;
            var bestDate = DateTime.MinValue;
            var bestFolder = string.Empty;
            var bestRoot = string.Empty;
            var diagnostics = new List<string>();
            foreach (var root in existingRoots)
            {
                if (!TryFindNewestDatedSubfolder(root, out var candidateFolder, out var candidateDate, out var rootError))
                {
                    diagnostics.Add(rootError);
                    continue;
                }

                if (!foundAny || candidateDate > bestDate)
                {
                    foundAny = true;
                    bestDate = candidateDate;
                    bestFolder = candidateFolder;
                    bestRoot = root;
                }
            }

            if (!foundAny)
            {
                error = "No dated folders were found in available roots.\n" + string.Join("\n", diagnostics);
                return false;
            }

            selectedSourceRoot = bestRoot;
            newestFolder = bestFolder;
            newestDate = bestDate;
            return true;
        }

        private static bool TryFindNewestDidsFolder(string sourceRoot, out string newestFolder, out DateTime newestDate, out string error)
        {
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                error = $"Source root not found: {sourceRoot}";
                return false;
            }

            var candidates = new List<(string FolderPath, DateTime Date)>();
            foreach (var folder in Directory.GetDirectories(sourceRoot, "dids_*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder) ?? string.Empty;
                if (TryParseDateFromFolderName(name, out var parsedDate))
                {
                    candidates.Add((folder, parsedDate));
                }
            }

            if (candidates.Count == 0)
            {
                error = $"No dated dids_* folders found under:\n{sourceRoot}";
                return false;
            }

            var selected = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => Path.GetFileName(c.FolderPath), StringComparer.OrdinalIgnoreCase)
                .First();

            newestFolder = selected.FolderPath;
            newestDate = selected.Date;
            return true;
        }

        private static bool TryFindNewestDatedSubfolder(string sourceRoot, out string newestFolder, out DateTime newestDate, out string error)
        {
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                error = $"Source root not found: {sourceRoot}";
                return false;
            }

            var candidates = new List<(string FolderPath, DateTime Date)>();
            foreach (var folder in Directory.GetDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder) ?? string.Empty;
                if (TryParseDateFromFolderName(name, out var parsedDate))
                {
                    candidates.Add((folder, parsedDate));
                }
            }

            if (candidates.Count == 0)
            {
                error = $"No dated folders found under:\n{sourceRoot}";
                return false;
            }

            var selected = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => Path.GetFileName(c.FolderPath), StringComparer.OrdinalIgnoreCase)
                .First();

            newestFolder = selected.FolderPath;
            newestDate = selected.Date;
            return true;
        }

        private static bool TryParseDateFromFolderName(string folderName, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            var match = System.Text.RegularExpressions.Regex.Match(folderName, @"(?<a>\d{1,2})-(?<b>\d{1,2})-(?<y>\d{2,4})");
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["a"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first) ||
                !int.TryParse(match.Groups["b"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second) ||
                !int.TryParse(match.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return false;
            }

            if (year < 100)
            {
                year += 2000;
            }

            int month;
            int day;
            if (first > 12 && second <= 12)
            {
                day = first;
                month = second;
            }
            else if (second > 12 && first <= 12)
            {
                month = first;
                day = second;
            }
            else
            {
                day = first;
                month = second;
            }

            if (month < 1 || month > 12 || day < 1)
            {
                return false;
            }

            var maxDay = DateTime.DaysInMonth(year, month);
            if (day > maxDay)
            {
                return false;
            }

            date = new DateTime(year, month, day);
            return true;
        }

        private static bool DirectoryContentsDiffer(string sourceDirectory, string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return true;
            }

            if (!Directory.Exists(destinationDirectory))
            {
                return true;
            }

            var src = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => BuildFileSignature(sourceDirectory, path))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var dst = Directory.GetFiles(destinationDirectory, "*", SearchOption.AllDirectories)
                .Select(path => BuildFileSignature(destinationDirectory, path))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return !src.SequenceEqual(dst, StringComparer.OrdinalIgnoreCase);
        }

        private static bool DirectoryContentsDifferForBaseNames(
            string sourceDirectory,
            string destinationDirectory,
            IReadOnlyList<string> baseNames)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return true;
            }

            if (!Directory.Exists(destinationDirectory))
            {
                return true;
            }

            var selectedBaseNames = new HashSet<string>(
                baseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (selectedBaseNames.Count == 0)
            {
                return true;
            }

            var sourceFilesByName = ResolveSelectedShapeSourceFiles(sourceDirectory, selectedBaseNames, out _, useDeepValidation: false);
            if (sourceFilesByName.Count == 0)
            {
                return true;
            }

            var destinationFilesByName = Directory.GetFiles(destinationDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => selectedBaseNames.Contains(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                .Where(path => IsTrackedShapeUpdateExtension(Path.GetExtension(path) ?? string.Empty))
                .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var sourcePair in sourceFilesByName)
            {
                if (!destinationFilesByName.TryGetValue(sourcePair.Key, out var destinationPath))
                {
                    return true;
                }

                if (GetSafeFileLength(sourcePair.Value) != GetSafeFileLength(destinationPath))
                {
                    return true;
                }

                if (GetSafeLastWriteTimeUtcTicks(sourcePair.Value) > GetSafeLastWriteTimeUtcTicks(destinationPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildFileSignature(string root, string fullPath)
        {
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            var info = new FileInfo(fullPath);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                relative,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
        }

        private static string BuildFlatFileSignature(string fileName, string fullPath)
        {
            var info = new FileInfo(fullPath);
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                fileName,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
        }

        private static Dictionary<string, string> ResolveSelectedShapeSourceFiles(
            string sourceDirectory,
            IReadOnlyCollection<string> shapeBaseNames,
            out List<string> missingBaseNames,
            bool useDeepValidation = true)
        {
            missingBaseNames = new List<string>();
            var selectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(sourceDirectory))
            {
                missingBaseNames.AddRange(shapeBaseNames ?? Array.Empty<string>());
                return selectedFiles;
            }

            var requestedBaseNames = new HashSet<string>(
                shapeBaseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (requestedBaseNames.Count == 0)
            {
                return selectedFiles;
            }

            // Resolve one best .shp anchor per requested base name. Do a cheap top-level
            // pass first, then a single recursive pass only for still-missing bases.
            var bestAnchorByBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shpPath in Directory.EnumerateFiles(sourceDirectory, "*.shp", SearchOption.TopDirectoryOnly))
            {
                var baseName = Path.GetFileNameWithoutExtension(shpPath) ?? string.Empty;
                if (!requestedBaseNames.Contains(baseName))
                {
                    continue;
                }

                if (!IsShapefileAnchorValid(shpPath, useDeepValidation))
                {
                    continue;
                }

                TrySetNewerAnchor(bestAnchorByBaseName, baseName, shpPath);
            }

            if (bestAnchorByBaseName.Count < requestedBaseNames.Count)
            {
                foreach (var shpPath in Directory.EnumerateFiles(sourceDirectory, "*.shp", SearchOption.AllDirectories))
                {
                    var baseName = Path.GetFileNameWithoutExtension(shpPath) ?? string.Empty;
                    if (!requestedBaseNames.Contains(baseName))
                    {
                        continue;
                    }

                    if (!IsShapefileAnchorValid(shpPath, useDeepValidation))
                    {
                        continue;
                    }

                    TrySetNewerAnchor(bestAnchorByBaseName, baseName, shpPath);
                }
            }

            foreach (var baseName in requestedBaseNames)
            {
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    continue;
                }

                if (!bestAnchorByBaseName.TryGetValue(baseName, out var anchorPath) || string.IsNullOrWhiteSpace(anchorPath))
                {
                    missingBaseNames.Add(baseName);
                    continue;
                }

                var anchorFolder = Path.GetDirectoryName(anchorPath) ?? sourceDirectory;
                var matches = Directory.GetFiles(anchorFolder, baseName + ".*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path), baseName, StringComparison.OrdinalIgnoreCase))
                    .Where(path => IsTrackedShapeUpdateExtension(Path.GetExtension(path) ?? string.Empty))
                    .ToList();
                if (matches.Count == 0)
                {
                    matches.Add(anchorPath);
                }

                foreach (var path in matches)
                {
                    var fileName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    if (!selectedFiles.TryGetValue(fileName, out var existingPath))
                    {
                        selectedFiles[fileName] = path;
                        continue;
                    }

                    if (GetSafeLastWriteTimeUtcTicks(path) > GetSafeLastWriteTimeUtcTicks(existingPath))
                    {
                        selectedFiles[fileName] = path;
                    }
                }
            }

            return selectedFiles;
        }

        private static void TrySetNewerAnchor(
            IDictionary<string, string> anchorByBaseName,
            string baseName,
            string candidatePath)
        {
            if (anchorByBaseName == null || string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(candidatePath))
            {
                return;
            }

            if (!anchorByBaseName.TryGetValue(baseName, out var existingPath))
            {
                anchorByBaseName[baseName] = candidatePath;
                return;
            }

            if (GetSafeLastWriteTimeUtcTicks(candidatePath) > GetSafeLastWriteTimeUtcTicks(existingPath))
            {
                anchorByBaseName[baseName] = candidatePath;
            }
        }
        private static bool IsShapefileAnchorValid(string shapefilePath, bool useDeepValidation)
        {
            if (string.IsNullOrWhiteSpace(shapefilePath) || !File.Exists(shapefilePath))
            {
                return false;
            }

            var basePath = Path.Combine(
                Path.GetDirectoryName(shapefilePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(shapefilePath) ?? string.Empty);
            if (!File.Exists(basePath + ".shx") || !File.Exists(basePath + ".dbf"))
            {
                return false;
            }

            if (!useDeepValidation)
            {
                return true;
            }

            return TryValidateShapefileSet(shapefilePath, out _);
        }
        private static bool IsMatchSetValidForShapefile(IReadOnlyList<string> matches)
        {
            if (matches == null || matches.Count == 0)
            {
                return true;
            }

            var shpPath = matches.FirstOrDefault(path =>
                string.Equals(Path.GetExtension(path), ".shp", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(shpPath))
            {
                return true;
            }

            return TryValidateShapefileSet(shpPath, out _);
        }

        private static bool TryValidateShapefileSet(string shapefilePath, out string failureReason)
        {
            failureReason = string.Empty;

            try
            {
                if (!File.Exists(shapefilePath))
                {
                    failureReason = "Missing .shp file.";
                    return false;
                }

                var basePath = Path.Combine(
                    Path.GetDirectoryName(shapefilePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(shapefilePath) ?? string.Empty);
                foreach (var ext in new[] { ".shx", ".dbf" })
                {
                    var sidecarPath = basePath + ext;
                    if (!File.Exists(sidecarPath))
                    {
                        failureReason = "Missing required sidecar: " + sidecarPath;
                        return false;
                    }
                }

                using (var shpStream = new FileStream(shapefilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var shpReader = new BinaryReader(shpStream))
                {
                    if (shpStream.Length < 100)
                    {
                        failureReason = "Shapefile is shorter than 100-byte header.";
                        return false;
                    }

                    var fileCode = ReadInt32BigEndian(shpReader);
                    if (fileCode != 9994)
                    {
                        failureReason = $"Unexpected SHP file code {fileCode}.";
                        return false;
                    }

                    shpStream.Seek(24, SeekOrigin.Begin);
                    var fileLengthWords = ReadInt32BigEndian(shpReader);
                    var expectedLengthBytes = (long)fileLengthWords * 2L;
                    if (expectedLengthBytes != shpStream.Length)
                    {
                        failureReason = $"Header length mismatch (header={expectedLengthBytes}, actual={shpStream.Length}).";
                        return false;
                    }

                    long offset = 100;
                    shpStream.Seek(offset, SeekOrigin.Begin);
                    while (offset < shpStream.Length)
                    {
                        if (shpStream.Length - offset < 8)
                        {
                            failureReason = $"Truncated record header at byte {offset}.";
                            return false;
                        }

                        _ = ReadInt32BigEndian(shpReader);
                        var contentLengthWords = ReadInt32BigEndian(shpReader);
                        if (contentLengthWords < 2)
                        {
                            failureReason = $"Invalid record length {contentLengthWords} words at byte {offset}.";
                            return false;
                        }

                        var contentLengthBytes = (long)contentLengthWords * 2L;
                        var nextOffset = offset + 8L + contentLengthBytes;
                        if (nextOffset > shpStream.Length)
                        {
                            failureReason = $"Truncated record body at byte {offset} (needs {contentLengthBytes} bytes).";
                            return false;
                        }

                        offset = nextOffset;
                        shpStream.Seek(offset, SeekOrigin.Begin);
                    }
                }

                var shxPath = basePath + ".shx";
                using (var shxStream = new FileStream(shxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var shxReader = new BinaryReader(shxStream))
                {
                    if (shxStream.Length < 100 || ((shxStream.Length - 100) % 8) != 0)
                    {
                        failureReason = $"Invalid SHX file size {shxStream.Length}.";
                        return false;
                    }

                    var fileCode = ReadInt32BigEndian(shxReader);
                    if (fileCode != 9994)
                    {
                        failureReason = $"Unexpected SHX file code {fileCode}.";
                        return false;
                    }

                    shxStream.Seek(24, SeekOrigin.Begin);
                    var fileLengthWords = ReadInt32BigEndian(shxReader);
                    var expectedLengthBytes = (long)fileLengthWords * 2L;
                    if (expectedLengthBytes != shxStream.Length)
                    {
                        failureReason = $"SHX header length mismatch (header={expectedLengthBytes}, actual={shxStream.Length}).";
                        return false;
                    }
                }

                return true;
            }
            catch (System.Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
            {
                throw new EndOfStreamException("Unable to read 4-byte integer.");
            }

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToInt32(bytes, 0);
        }

        private static long GetSafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return -1L;
            }
        }

        private static long GetSafeLastWriteTimeUtcTicks(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch
            {
                return DateTime.MinValue.Ticks;
            }
        }

        private static string[] BuildShapeBaseNamesFromShapefileNames(IEnumerable<string>? shapefileNames, params string[] fallbackBaseNames)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (shapefileNames != null)
            {
                foreach (var value in shapefileNames)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var baseName = Path.GetFileNameWithoutExtension(value.Trim());
                    if (!string.IsNullOrWhiteSpace(baseName))
                    {
                        set.Add(baseName);
                    }
                }
            }

            if (set.Count == 0 && fallbackBaseNames != null)
            {
                foreach (var fallback in fallbackBaseNames)
                {
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        set.Add(fallback.Trim());
                    }
                }
            }

            return set.ToArray();
        }

        private static int ReplaceDirectoryContents(string sourceDirectory, string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source folder not found: " + sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var copiedCount = 0;
            foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                var destinationFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
                copiedCount++;
            }

            return copiedCount;
        }

        private static int ReplaceDirectoryContentsWithSelectedShapeSets(
            string sourceDirectory,
            string destinationDirectory,
            IReadOnlyList<string> shapeBaseNames)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source folder not found: " + sourceDirectory);
            }

            var selectedBaseNames = new HashSet<string>(
                shapeBaseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (selectedBaseNames.Count == 0)
            {
                throw new InvalidOperationException("No shape base names configured for selected shape-set copy.");
            }

            var sourceFilesByName = ResolveSelectedShapeSourceFiles(sourceDirectory, selectedBaseNames, out var missingBaseNames, useDeepValidation: true);
            if (missingBaseNames.Count > 0)
            {
                throw new FileNotFoundException(
                    $"Missing selected shape set(s) in source '{sourceDirectory}': {string.Join(", ", missingBaseNames)}");
            }

            if (sourceFilesByName.Count == 0)
            {
                throw new FileNotFoundException(
                    $"No files found for selected shape set(s) in source '{sourceDirectory}': {string.Join(", ", selectedBaseNames)}");
            }

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var copiedCount = 0;
            foreach (var pair in sourceFilesByName)
            {
                var destinationPath = Path.Combine(destinationDirectory, pair.Key);
                File.Copy(pair.Value, destinationPath, overwrite: true);
                copiedCount++;
            }

            return copiedCount;
        }

        private static void ClearDirectoryContents(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            var directory = new DirectoryInfo(directoryPath);
            foreach (var file in directory.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                file.IsReadOnly = false;
                file.Delete();
            }

            foreach (var childDirectory in directory.GetDirectories("*", SearchOption.TopDirectoryOnly))
            {
                childDirectory.Delete(recursive: true);
            }
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

        private static List<Extents2d> BuildScopeExtents(
            Database database,
            IReadOnlyList<ObjectId> scopePolylineIds,
            double buffer)
        {
            var extents = new List<Extents2d>();
            if (database == null || scopePolylineIds == null || scopePolylineIds.Count == 0)
            {
                return extents;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var id in scopePolylineIds)
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

        private static List<Extents3d> BuildScopeWindows(IReadOnlyList<Extents2d>? scopeExtents)
        {
            var windows = new List<Extents3d>();
            if (scopeExtents == null)
            {
                return windows;
            }

            for (var i = 0; i < scopeExtents.Count; i++)
            {
                var extent = scopeExtents[i];
                windows.Add(new Extents3d(
                    new Point3d(extent.MinPoint.X, extent.MinPoint.Y, 0.0),
                    new Point3d(extent.MaxPoint.X, extent.MaxPoint.Y, 0.0)));
            }

            return windows;
        }

        private enum ScopeOverlapKind
        {
            None,
            Partial,
            FullyInside,
        }

        private static ScopeOverlapKind ClassifyEntityScopeOverlap(Entity ent, IReadOnlyList<Extents2d>? scopeExtents)
        {
            if (ent == null || scopeExtents == null || scopeExtents.Count == 0)
            {
                return ScopeOverlapKind.None;
            }

            Extents3d ge;
            try
            {
                ge = ent.GeometricExtents;
            }
            catch
            {
                return ScopeOverlapKind.None;
            }

            var e2d = new Extents2d(ge.MinPoint.X, ge.MinPoint.Y, ge.MaxPoint.X, ge.MaxPoint.Y);
            var overlaps = false;
            for (var i = 0; i < scopeExtents.Count; i++)
            {
                var scopeExtent = scopeExtents[i];
                if (e2d.MaxPoint.X < scopeExtent.MinPoint.X ||
                    e2d.MinPoint.X > scopeExtent.MaxPoint.X ||
                    e2d.MaxPoint.Y < scopeExtent.MinPoint.Y ||
                    e2d.MinPoint.Y > scopeExtent.MaxPoint.Y)
                {
                    continue;
                }

                overlaps = true;
                if (e2d.MinPoint.X >= scopeExtent.MinPoint.X &&
                    e2d.MaxPoint.X <= scopeExtent.MaxPoint.X &&
                    e2d.MinPoint.Y >= scopeExtent.MinPoint.Y &&
                    e2d.MaxPoint.Y <= scopeExtent.MaxPoint.Y)
                {
                    return ScopeOverlapKind.FullyInside;
                }
            }

            return overlaps ? ScopeOverlapKind.Partial : ScopeOverlapKind.None;
        }

        private static bool DoesEntityActuallyTouchScopeWindows(
            Entity entity,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            if (entity == null || scopeWindows == null || scopeWindows.Count == 0)
            {
                return false;
            }

            if (TryExtractOpenPathVertices(entity, out var openPathVertices))
            {
                return DoesOpenPathTouchScopeWindows(openPathVertices, scopeWindows);
            }

            if (entity is Polyline polyline && polyline.Closed)
            {
                return DoesClosedBoundaryTouchScopeWindows(polyline, scopeWindows);
            }

            if (!GeometryUtils.TryGetClosedBoundaryClone(entity, out var boundaryClone))
            {
                return false;
            }

            using (boundaryClone)
            {
                return DoesClosedBoundaryTouchScopeWindows(boundaryClone, scopeWindows);
            }
        }

        private static bool ShouldKeepWholePartialP3Entity(
            Entity entity,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            if (entity == null || scopeWindows == null || scopeWindows.Count == 0)
            {
                return false;
            }

            // Partial closed boundaries would still visibly extend outside the work area,
            // so they should not survive whole when trimming is disabled.
            if (entity is Polyline closedPolyline && closedPolyline.Closed)
            {
                return false;
            }

            if (!TryExtractOpenPathVertices(entity, out var openPathVertices) ||
                openPathVertices.Count < 2)
            {
                return false;
            }

            const double scopePointTolerance = 0.05;
            return IsPointInsideAnyScopeWindow(openPathVertices[0], scopeWindows, scopePointTolerance) ||
                   IsPointInsideAnyScopeWindow(openPathVertices[openPathVertices.Count - 1], scopeWindows, scopePointTolerance);
        }

        private static bool RequiresActualTouchValidation(Entity entity)
        {
            if (entity == null)
            {
                return false;
            }

            return entity is Line ||
                   (entity is Polyline polyline && !polyline.Closed);
        }

        private static bool TryReplacePathEntityWithScopeClippedPieces(
            Entity entity,
            Transaction transaction,
            BlockTableRecord modelSpace,
            IReadOnlyList<Extents3d> scopeWindows,
            string outputLayer,
            bool allowClosedPolyline,
            string xDataAppName,
            string xDataString,
            out int replacementCount,
            out bool filteredOut)
        {
            replacementCount = 0;
            filteredOut = false;

            if (entity == null ||
                transaction == null ||
                modelSpace == null ||
                scopeWindows == null ||
                scopeWindows.Count == 0 ||
                !TryExtractClippablePathVertices(entity, allowClosedPolyline, out var vertices))
            {
                return false;
            }

            var clippedPaths = ClipOpenPathToScopeWindows(vertices, scopeWindows);
            entity.Erase(true);
            if (clippedPaths.Count == 0)
            {
                filteredOut = true;
                return true;
            }

            for (var i = 0; i < clippedPaths.Count; i++)
            {
                var polyline = CreateOpenPolyline(clippedPaths[i], outputLayer);
                modelSpace.AppendEntity(polyline);
                transaction.AddNewlyCreatedDBObject(polyline, true);
                MarkEntityWithImportTag(modelSpace.Database, transaction, polyline, xDataAppName, xDataString);
                replacementCount++;
            }

            return true;
        }

        private static bool TryReplaceOpenPathEntityWithOutsideScopePieces(
            Entity entity,
            Transaction transaction,
            BlockTableRecord modelSpace,
            IReadOnlyList<Extents3d> scopeWindows,
            string outputLayer,
            string xDataAppName,
            string xDataString,
            out int replacementCount,
            out bool filteredOut)
        {
            replacementCount = 0;
            filteredOut = false;

            if (entity == null ||
                transaction == null ||
                modelSpace == null ||
                scopeWindows == null ||
                scopeWindows.Count == 0 ||
                !TryExtractOpenPathVertices(entity, out var vertices))
            {
                return false;
            }

            var outsidePaths = ClipOpenPathOutsideScopeWindows(vertices, scopeWindows);
            entity.Erase(true);
            if (outsidePaths.Count == 0)
            {
                filteredOut = true;
                return true;
            }

            for (var i = 0; i < outsidePaths.Count; i++)
            {
                var polyline = CreateOpenPolyline(outsidePaths[i], outputLayer);
                modelSpace.AppendEntity(polyline);
                transaction.AddNewlyCreatedDBObject(polyline, true);
                MarkEntityWithImportTag(modelSpace.Database, transaction, polyline, xDataAppName, xDataString);
                replacementCount++;
            }

            return true;
        }

        private static bool TryExtractClippablePathVertices(
            Entity entity,
            bool allowClosedPolyline,
            out List<Point2d> vertices)
        {
            vertices = new List<Point2d>();
            if (entity == null)
            {
                return false;
            }

            if (entity is Line line)
            {
                vertices.Add(new Point2d(line.StartPoint.X, line.StartPoint.Y));
                vertices.Add(new Point2d(line.EndPoint.X, line.EndPoint.Y));
                return true;
            }

            if (entity is not Polyline polyline || polyline.NumberOfVertices < 2)
            {
                return false;
            }

            if (polyline.Closed && !allowClosedPolyline)
            {
                return false;
            }

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                vertices.Add(polyline.GetPoint2dAt(i));
            }

            if (polyline.Closed)
            {
                vertices.Add(vertices[0]);
            }

            return vertices.Count >= 2;
        }

        private static bool TryExtractOpenPathVertices(Entity entity, out List<Point2d> vertices)
        {
            vertices = new List<Point2d>();
            if (entity == null)
            {
                return false;
            }

            if (entity is Line line)
            {
                vertices.Add(new Point2d(line.StartPoint.X, line.StartPoint.Y));
                vertices.Add(new Point2d(line.EndPoint.X, line.EndPoint.Y));
                return true;
            }

            if (entity is not Polyline polyline || polyline.Closed || polyline.NumberOfVertices < 2)
            {
                return false;
            }

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                vertices.Add(polyline.GetPoint2dAt(i));
            }

            return vertices.Count >= 2;
        }

        private static bool DoesOpenPathTouchScopeWindows(
            IReadOnlyList<Point2d> vertices,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            if (vertices == null || vertices.Count < 2 || scopeWindows == null || scopeWindows.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < vertices.Count - 1; i++)
            {
                var a = vertices[i];
                var b = vertices[i + 1];
                for (var wi = 0; wi < scopeWindows.Count; wi++)
                {
                    if (TryClipSegmentToWindow(a, b, scopeWindows[wi], out _, out _))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<List<Point2d>> ClipOpenPathToScopeWindows(
            IReadOnlyList<Point2d> vertices,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            var paths = new List<List<Point2d>>();
            if (vertices == null || vertices.Count < 2 || scopeWindows == null || scopeWindows.Count == 0)
            {
                return paths;
            }

            const double pointMergeTolerance = 1e-4;
            List<Point2d>? currentPath = null;

            void AppendPoint(List<Point2d> path, Point2d point)
            {
                if (path.Count == 0 || path[path.Count - 1].GetDistanceTo(point) > pointMergeTolerance)
                {
                    path.Add(point);
                }
            }

            void FlushCurrentPath()
            {
                if (currentPath != null && currentPath.Count >= 2)
                {
                    paths.Add(currentPath);
                }

                currentPath = null;
            }

            for (var i = 0; i < vertices.Count - 1; i++)
            {
                var a = vertices[i];
                var b = vertices[i + 1];
                var clipped = false;
                var clipStart = default(Point2d);
                var clipEnd = default(Point2d);

                for (var wi = 0; wi < scopeWindows.Count; wi++)
                {
                    if (!TryClipSegmentToWindow(a, b, scopeWindows[wi], out clipStart, out clipEnd))
                    {
                        continue;
                    }

                    clipped = true;
                    break;
                }

                if (!clipped)
                {
                    FlushCurrentPath();
                    continue;
                }

                if (currentPath == null)
                {
                    currentPath = new List<Point2d>();
                }
                else if (currentPath[currentPath.Count - 1].GetDistanceTo(clipStart) > pointMergeTolerance)
                {
                    FlushCurrentPath();
                    currentPath = new List<Point2d>();
                }

                AppendPoint(currentPath, clipStart);
                AppendPoint(currentPath, clipEnd);
            }

            FlushCurrentPath();
            return paths;
        }

        private static List<List<Point2d>> ClipOpenPathOutsideScopeWindows(
            IReadOnlyList<Point2d> vertices,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            var paths = new List<List<Point2d>>();
            if (vertices == null || vertices.Count < 2 || scopeWindows == null || scopeWindows.Count == 0)
            {
                return paths;
            }

            const double pointMergeTolerance = 1e-4;
            const double parameterTolerance = 1e-9;
            List<Point2d>? currentPath = null;

            void AppendPoint(List<Point2d> path, Point2d point)
            {
                if (path.Count == 0 || path[path.Count - 1].GetDistanceTo(point) > pointMergeTolerance)
                {
                    path.Add(point);
                }
            }

            void FlushCurrentPath()
            {
                if (currentPath != null && currentPath.Count >= 2)
                {
                    paths.Add(currentPath);
                }

                currentPath = null;
            }

            for (var i = 0; i < vertices.Count - 1; i++)
            {
                var a = vertices[i];
                var b = vertices[i + 1];
                var outsideIntervals = GetOutsideSegmentIntervals(a, b, scopeWindows);
                if (outsideIntervals.Count == 0)
                {
                    FlushCurrentPath();
                    continue;
                }

                for (var intervalIndex = 0; intervalIndex < outsideIntervals.Count; intervalIndex++)
                {
                    var interval = outsideIntervals[intervalIndex];
                    var segmentStart = InterpolateSegmentPoint(a, b, interval.Start);
                    var segmentEnd = InterpolateSegmentPoint(a, b, interval.End);

                    if (currentPath == null)
                    {
                        currentPath = new List<Point2d>();
                    }
                    else if (currentPath[currentPath.Count - 1].GetDistanceTo(segmentStart) > pointMergeTolerance)
                    {
                        FlushCurrentPath();
                        currentPath = new List<Point2d>();
                    }

                    AppendPoint(currentPath, segmentStart);
                    AppendPoint(currentPath, segmentEnd);

                    if (interval.End < (1.0 - parameterTolerance))
                    {
                        FlushCurrentPath();
                    }
                }
            }

            FlushCurrentPath();
            return paths;
        }

        private static List<(double Start, double End)> GetOutsideSegmentIntervals(
            Point2d start,
            Point2d end,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            const double parameterTolerance = 1e-9;
            var insideIntervals = new List<(double Start, double End)>();

            for (var wi = 0; wi < scopeWindows.Count; wi++)
            {
                if (!TryClipSegmentToWindow(start, end, scopeWindows[wi], out var clippedStart, out var clippedEnd))
                {
                    continue;
                }

                var startParam = ComputeSegmentParameter(start, end, clippedStart);
                var endParam = ComputeSegmentParameter(start, end, clippedEnd);
                if (endParam < startParam)
                {
                    var temp = startParam;
                    startParam = endParam;
                    endParam = temp;
                }

                startParam = Math.Max(0.0, Math.Min(1.0, startParam));
                endParam = Math.Max(0.0, Math.Min(1.0, endParam));
                if ((endParam - startParam) <= parameterTolerance)
                {
                    continue;
                }

                insideIntervals.Add((startParam, endParam));
            }

            if (insideIntervals.Count == 0)
            {
                return new List<(double Start, double End)> { (0.0, 1.0) };
            }

            insideIntervals.Sort((left, right) => left.Start.CompareTo(right.Start));
            var mergedInside = new List<(double Start, double End)>();
            for (var i = 0; i < insideIntervals.Count; i++)
            {
                var interval = insideIntervals[i];
                if (mergedInside.Count == 0 ||
                    interval.Start > (mergedInside[mergedInside.Count - 1].End + parameterTolerance))
                {
                    mergedInside.Add(interval);
                    continue;
                }

                var previous = mergedInside[mergedInside.Count - 1];
                mergedInside[mergedInside.Count - 1] = (previous.Start, Math.Max(previous.End, interval.End));
            }

            var outsideIntervals = new List<(double Start, double End)>();
            var previousEnd = 0.0;
            for (var i = 0; i < mergedInside.Count; i++)
            {
                var interval = mergedInside[i];
                if (interval.Start > (previousEnd + parameterTolerance))
                {
                    outsideIntervals.Add((previousEnd, interval.Start));
                }

                previousEnd = Math.Max(previousEnd, interval.End);
            }

            if (previousEnd < (1.0 - parameterTolerance))
            {
                outsideIntervals.Add((previousEnd, 1.0));
            }

            return outsideIntervals;
        }

        private static double ComputeSegmentParameter(Point2d start, Point2d end, Point2d point)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            if (Math.Abs(dx) >= Math.Abs(dy) && Math.Abs(dx) > 1e-9)
            {
                return (point.X - start.X) / dx;
            }

            if (Math.Abs(dy) > 1e-9)
            {
                return (point.Y - start.Y) / dy;
            }

            return 0.0;
        }

        private static Point2d InterpolateSegmentPoint(Point2d start, Point2d end, double parameter)
        {
            return new Point2d(
                start.X + ((end.X - start.X) * parameter),
                start.Y + ((end.Y - start.Y) * parameter));
        }

        private static Polyline CreateOpenPolyline(IReadOnlyList<Point2d> points, string outputLayer)
        {
            var polyline = new Polyline(points.Count);
            for (var i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
            }

            polyline.Closed = false;
            polyline.Layer = outputLayer;
            polyline.ColorIndex = 256;
            return polyline;
        }

        private static bool IsPointInsideAnyScopeWindow(
            Point2d point,
            IReadOnlyList<Extents3d> scopeWindows,
            double tolerance = 0.0)
        {
            if (scopeWindows == null || scopeWindows.Count == 0)
            {
                return false;
            }

            for (var wi = 0; wi < scopeWindows.Count; wi++)
            {
                var window = scopeWindows[wi];
                if (point.X >= (window.MinPoint.X - tolerance) &&
                    point.X <= (window.MaxPoint.X + tolerance) &&
                    point.Y >= (window.MinPoint.Y - tolerance) &&
                    point.Y <= (window.MaxPoint.Y + tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DoesClosedBoundaryTouchScopeWindows(
            Polyline boundary,
            IReadOnlyList<Extents3d> scopeWindows)
        {
            if (boundary == null || scopeWindows == null || scopeWindows.Count == 0)
            {
                return false;
            }

            for (var wi = 0; wi < scopeWindows.Count; wi++)
            {
                if (DoesClosedBoundaryTouchWindow(boundary, scopeWindows[wi]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DoesClosedBoundaryTouchWindow(Polyline boundary, Extents3d window)
        {
            for (var i = 0; i < boundary.NumberOfVertices; i++)
            {
                var vertex = boundary.GetPoint2dAt(i);
                if (vertex.X >= window.MinPoint.X &&
                    vertex.X <= window.MaxPoint.X &&
                    vertex.Y >= window.MinPoint.Y &&
                    vertex.Y <= window.MaxPoint.Y)
                {
                    return true;
                }
            }

            var windowCenter = new Point2d(
                0.5 * (window.MinPoint.X + window.MaxPoint.X),
                0.5 * (window.MinPoint.Y + window.MaxPoint.Y));
            if (GeometryUtils.IsPointInsidePolyline(boundary, windowCenter))
            {
                return true;
            }

            using var rect = BuildWindowPolyline(window);
            return GeometryUtils.TryIntersectPolylines(boundary, rect, out var clippedPieces) &&
                   clippedPieces != null &&
                   clippedPieces.Count > 0;
        }

        private static Polyline BuildWindowPolyline(Extents3d window)
        {
            var rect = new Polyline(4);
            rect.AddVertexAt(0, new Point2d(window.MinPoint.X, window.MinPoint.Y), 0.0, 0.0, 0.0);
            rect.AddVertexAt(1, new Point2d(window.MaxPoint.X, window.MinPoint.Y), 0.0, 0.0, 0.0);
            rect.AddVertexAt(2, new Point2d(window.MaxPoint.X, window.MaxPoint.Y), 0.0, 0.0, 0.0);
            rect.AddVertexAt(3, new Point2d(window.MinPoint.X, window.MaxPoint.Y), 0.0, 0.0, 0.0);
            rect.Closed = true;
            return rect;
        }

        private static bool IsPolygonLikeEntity(Entity ent)
        {
            if (ent == null)
            {
                return false;
            }

            var dxf = ent.GetRXClass()?.DxfName ?? string.Empty;
            var cls = ent.GetType().Name ?? string.Empty;
            if (string.Equals(dxf, "MPOLYGON", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dxf, "POLYGON", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (cls.IndexOf("MPolygon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cls.IndexOf("Polygon", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static Polyline? TryConvertPolygonEntityToPolyline(
            Entity polygonEntity,
            Transaction tr,
            BlockTableRecord modelSpace,
            Logger? logger)
        {
            if (polygonEntity == null || tr == null || modelSpace == null)
            {
                return null;
            }

            var exploded = new DBObjectCollection();
            try
            {
                polygonEntity.Explode(exploded);
                Polyline? best = null;
                var bestArea = -1.0;
                foreach (DBObject dbo in exploded)
                {
                    if (dbo is not Polyline pl || !pl.Closed)
                    {
                        continue;
                    }

                    double area;
                    try
                    {
                        area = Math.Abs(pl.Area);
                    }
                    catch
                    {
                        area = 0.0;
                    }

                    if (area > bestArea)
                    {
                        best = pl;
                        bestArea = area;
                    }
                }

                if (best == null)
                {
                    return null;
                }

                var newPolyline = (Polyline)best.Clone();
                try
                {
                    newPolyline.ConstantWidth = 0.0;
                    for (var i = 0; i < newPolyline.NumberOfVertices; i++)
                    {
                        newPolyline.SetStartWidthAt(i, 0.0);
                        newPolyline.SetEndWidthAt(i, 0.0);
                    }
                }
                catch
                {
                    // ignore width normalization failures
                }

                modelSpace.AppendEntity(newPolyline);
                tr.AddNewlyCreatedDBObject(newPolyline, true);

                try
                {
                    polygonEntity.Erase(true);
                }
                catch (System.Exception ex)
                {
                    logger?.WriteLine("P3 polygon erase failed after conversion: " + ex.Message);
                }

                return newPolyline;
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("P3 polygon conversion failed: " + ex.Message);
                return null;
            }
            finally
            {
                foreach (DBObject dbo in exploded)
                {
                    try
                    {
                        dbo.Dispose();
                    }
                    catch
                    {
                        // ignore dispose failures
                    }
                }
            }
        }

        private static void TrySetImporterLocationWindow(Importer importer, List<Extents2d> scopeExtents, Logger? logger)
        {
            if (importer == null || scopeExtents == null || scopeExtents.Count == 0)
            {
                logger?.WriteLine("P3 location window skipped: no requested work area extents.");
                return;
            }

            var union = UnionExtents(scopeExtents);
            var minX = union.MinPoint.X;
            var minY = union.MinPoint.Y;
            var maxX = union.MaxPoint.X;
            var maxY = union.MaxPoint.Y;
            try
            {
                var method = importer.GetType().GetMethod("SetLocationWindowAndOptions");
                if (method == null)
                {
                    logger?.WriteLine("P3 location window unsupported: SetLocationWindowAndOptions not found.");
                    return;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 5 ||
                    parameters[0].ParameterType != typeof(double) ||
                    parameters[1].ParameterType != typeof(double) ||
                    parameters[2].ParameterType != typeof(double) ||
                    parameters[3].ParameterType != typeof(double) ||
                    !parameters[4].ParameterType.IsEnum)
                {
                    logger?.WriteLine("P3 location window unsupported: unexpected SetLocationWindowAndOptions signature.");
                    return;
                }

                var option = GetEnumValue(parameters[4].ParameterType, 2, "kUseLocationWindow", "UseLocationWindow");
                try
                {
                    logger?.WriteLine($"P3 importer location window apply start (xmin,ymin,xmax,ymax): X[{minX:G},{maxX:G}] Y[{minY:G},{maxY:G}]");
                    method.Invoke(importer, new object[]
                    {
                        minX,
                        minY,
                        maxX,
                        maxY,
                        option
                    });
                    logger?.WriteLine($"P3 importer location window set (xmin,ymin,xmax,ymax): X[{minX:G},{maxX:G}] Y[{minY:G},{maxY:G}]");
                    return;
                }
                catch (System.Exception exStandard)
                {
                    var standardMessage = exStandard.InnerException?.Message ?? exStandard.Message;

                    try
                    {
                        method.Invoke(importer, new object[]
                        {
                            minX,
                            maxX,
                            minY,
                            maxY,
                            option
                        });
                        logger?.WriteLine($"P3 importer location window set (xmin,xmax,ymin,ymax): X[{minX:G},{maxX:G}] Y[{minY:G},{maxY:G}]");
                        return;
                    }
                    catch (System.Exception exFallback)
                    {
                        var fallbackMessage = exFallback.InnerException?.Message ?? exFallback.Message;
                        logger?.WriteLine($"P3 location window setup failed: standard='{standardMessage}', fallback='{fallbackMessage}'.");
                        return;
                    }
                }
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
    }
}


