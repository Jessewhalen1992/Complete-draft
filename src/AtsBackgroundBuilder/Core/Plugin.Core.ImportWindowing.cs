using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ImportExport;
using AtsBackgroundBuilder.Dispositions;

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

        private const string DispositionShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\DISPOS";
        private const string CompassMappingShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\COMPASS MAPPING";
        private const string CrownReservationsShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\CLR";

        private static void AutoUpdateSelectedShapeSetsIfNeeded(AtsBuildInput input, Logger? logger)
        {
            if (input == null)
            {
                return;
            }

            try
            {
                var needsDisposition = input.IncludeDispositionLinework || input.IncludeDispositionLabels;
                var dispositionError = string.Empty;
                if (needsDisposition &&
                    TryResolveNewestDidsFolderAcrossRoots(
                        DispositionShapeUpdateSourceRoots,
                        out var sourceRoot,
                        out var newestFolder,
                        out var newestDate,
                        out dispositionError))
                {
                    if (DirectoryContentsDiffer(newestFolder, DispositionShapeDestinationFolder))
                    {
                        var copied = ReplaceDirectoryContents(newestFolder, DispositionShapeDestinationFolder);
                        logger?.WriteLine($"Shape auto-update: Disposition copied {copied} file(s) from '{newestFolder}' (root '{sourceRoot}', date {newestDate:yyyy-MM-dd}).");
                    }
                    else
                    {
                        logger?.WriteLine("Shape auto-update: Disposition already current.");
                    }
                }
                else if (needsDisposition)
                {
                    logger?.WriteLine("Shape auto-update: Disposition skipped. " + dispositionError);
                }

                var compassError = string.Empty;
                if (input.IncludeCompassMapping &&
                    TryResolveFirstExistingRootAcrossRoots(
                        CompassMappingShapeUpdateSourceRoots,
                        out var compassSource,
                        out compassError))
                {
                    if (DirectoryContentsDifferForBaseNames(compassSource, CompassMappingShapeDestinationFolder, CompassMappingShapeBaseNames))
                    {
                        var copied = ReplaceDirectoryContentsWithSelectedShapeSets(
                            compassSource,
                            CompassMappingShapeDestinationFolder,
                            CompassMappingShapeBaseNames);
                        logger?.WriteLine($"Shape auto-update: Compass Mapping copied {copied} file(s) from '{compassSource}'.");
                    }
                    else
                    {
                        logger?.WriteLine("Shape auto-update: Compass Mapping already current.");
                    }
                }
                else if (input.IncludeCompassMapping)
                {
                    logger?.WriteLine("Shape auto-update: Compass Mapping skipped. " + compassError);
                }

                var crownError = string.Empty;
                if (input.IncludeCrownReservations &&
                    TryResolveNewestDatedFolderAcrossRoots(
                        CrownReservationsShapeUpdateSourceRoots,
                        out var crownSourceRoot,
                        out var crownSourceFolder,
                        out var crownSourceDate,
                        out crownError))
                {
                    if (DirectoryContentsDifferForBaseNames(crownSourceFolder, CrownReservationsShapeDestinationFolder, CrownReservationsShapeBaseNames))
                    {
                        var copied = ReplaceDirectoryContentsWithSelectedShapeSets(
                            crownSourceFolder,
                            CrownReservationsShapeDestinationFolder,
                            CrownReservationsShapeBaseNames);
                        logger?.WriteLine($"Shape auto-update: Crown Reservations copied {copied} file(s) from '{crownSourceFolder}' (root '{crownSourceRoot}', date {crownSourceDate:yyyy-MM-dd}).");
                    }
                    else
                    {
                        logger?.WriteLine("Shape auto-update: Crown Reservations already current.");
                    }
                }
                else if (input.IncludeCrownReservations)
                {
                    logger?.WriteLine("Shape auto-update: Crown Reservations skipped. " + crownError);
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("Shape auto-update failed: " + ex.Message);
            }
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
            ClearLayerEntities(database, outputLayer, logger);
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
                var convertedPolygonCount = 0;
                var unconvertedPolygonCount = 0;

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

                        if (IsPolygonLikeEntity(ent))
                        {
                            var converted = TryConvertPolygonEntityToPolyline(ent, tr, modelSpace, logger);
                            if (converted != null)
                            {
                                ent = converted;
                                convertedPolygonCount++;
                            }
                            else
                            {
                                unconvertedPolygonCount++;
                            }
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

        private static void ClearLayerEntities(Database database, string layerName, Logger? logger)
        {
            if (database == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

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
                logger?.WriteLine($"Cleared {erased} existing entity/ies from layer '{layerName}' before import.");
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
            var src = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Where(path => selectedBaseNames.Contains(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                .Select(path => BuildFileSignature(sourceDirectory, path))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var dst = Directory.GetFiles(destinationDirectory, "*", SearchOption.AllDirectories)
                .Where(path => selectedBaseNames.Contains(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                .Select(path => BuildFileSignature(destinationDirectory, path))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return !src.SequenceEqual(dst, StringComparer.OrdinalIgnoreCase);
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

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var selectedBaseNames = new HashSet<string>(
                shapeBaseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var copiedCount = 0;
            foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(baseName) || !selectedBaseNames.Contains(baseName))
                {
                    continue;
                }

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
    }
}
