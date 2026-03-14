using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Diagnostics.CodeAnalysis;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ImportExport;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

using MapOpenMode = Autodesk.Gis.Map.Constants.OpenMode;
using MapDataType = Autodesk.Gis.Map.Constants.DataType;
using OdRecord = Autodesk.Gis.Map.ObjectData.Record;
using OdRecords = Autodesk.Gis.Map.ObjectData.Records;

namespace AtsBackgroundBuilder.Dispositions
{
    public sealed class ShapefileImportSummary
    {
        public int ImportedDispositions { get; set; }
        public int FilteredDispositions { get; set; }
        public int DedupedDispositions { get; set; }
        public int ImportFailures { get; set; }
    }

    public static class ShapefileImporter
    {
        // Native Map importer can hard-crash on extremely large DBF-backed sets even
        // with a location window. Keep the size guard available, but disabled by default
        // so normal production imports still run unless explicitly safety-gated.
        private const long MaxNativeImporterDbfBytesWithoutOverride = 512L * 1024L * 1024L;
        private const long MaxNativeImporterShpBytesWithoutOverride = 256L * 1024L * 1024L;
        private const int DefaultLargeImportChunkRecordCount = 10000;
        private const int MinLargeImportChunkRecordCount = 1000;
        private const int MaxLargeImportChunkRecordCount = 200000;
        private const string EnableSingleSubsetImportEnvVar = "ATSBUILD_ENABLE_SINGLE_SUBSET_IMPORT";
        private static readonly object IgnoredSystemVariableWriteWarningLock = new object();
        private static readonly HashSet<string> IgnoredSystemVariableWriteWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static ShapefileImportSummary ImportShapefiles(
            Database database,
            Editor editor,
            Logger logger,
            Config config,
            IReadOnlyList<ObjectId> scopePolylineIds,
            List<ObjectId> dispositionPolylines,
            double scopeBufferMeters = 0.0,
            int? utmZoneHint = null)
        {
            var summary = new ShapefileImportSummary();

            if (config.DispositionShapefiles == null || config.DispositionShapefiles.Length == 0)
            {
                logger.WriteLine("No disposition shapefiles configured.");
                return summary;
            }

            var scopeBuffer = Math.Max(0.0, scopeBufferMeters);
            var sectionExtents = BuildSectionBufferExtents(database, scopePolylineIds, scopeBuffer);
            if (sectionExtents.Count == 0)
            {
                logger.WriteLine("No section extents available for shapefile filtering.");
                return summary;
            }

            ZoomToSectionExtents(editor, sectionExtents, logger);

            var existingKeys = BuildExistingFeatureKeys(database, logger, sectionExtents);
            var existingCandidates = CaptureDispositionCandidateIds(database); // LWPOLYLINE + MPOLYGON

            var searchFolders = BuildShapefileSearchFolders(config);
            logger.WriteLine($"Shapefile search folders: {string.Join("; ", searchFolders)}");
            logger.WriteLine($"Section extents loaded: {sectionExtents.Count} (buffer {scopeBuffer}).");

            if (!TryGetMap3dImporter(logger, out var importer))
            {
                summary.ImportFailures += config.DispositionShapefiles.Length;
                return summary;
            }

            // Your suggestion: set MAPUSEMPOLYGON BEFORE import starts.
            // This is the safest way to avoid MPOLYGON + POLYDISPLAY altogether.
            object? prevMapUseMPolygon = null;
            bool mapUseMPolygonChanged = TrySetSystemVariable("MAPUSEMPOLYGON", 0, logger, out prevMapUseMPolygon);
            object? prevPolyDisplay = null;
            bool polyDisplayChanged = TrySetSystemVariable("POLYDISPLAY", 1, logger, out prevPolyDisplay);

            Autodesk.AutoCAD.Runtime.ProgressMeter? overallMeter = null;
            try
            {
                overallMeter = new Autodesk.AutoCAD.Runtime.ProgressMeter();
                overallMeter.SetLimit(config.DispositionShapefiles.Length);
                overallMeter.Start("ATSBUILD: Importing shapefiles");
            }
            catch
            {
                overallMeter = null;
            }

            try
            {
                foreach (var shapefile in config.DispositionShapefiles)
                {
                    try { overallMeter?.MeterProgress(); } catch { }

                    logger.WriteLine($"Resolving shapefile: {shapefile}");
                    var shapefilePath = ResolveShapefilePath(searchFolders, shapefile, logger);
                    if (string.IsNullOrWhiteSpace(shapefilePath))
                    {
                        logger.WriteLine($"Shapefile missing: {shapefile}. Searched: {string.Join("; ", searchFolders)}");
                        summary.ImportFailures++;
                        continue;
                    }

                    logger.WriteLine($"Using shapefile: {shapefilePath}");
                    LogShapefileSidecars(shapefilePath, logger);

                    if (ShouldSkipLargeNativeDispositionImport(shapefilePath, logger, out var largeSkipReason))
                    {
                        logger.WriteLine(largeSkipReason);
                        summary.ImportFailures++;
                        continue;
                    }

                    var importPaths = new List<string> { shapefilePath };
                    var temporaryImportFolders = new List<string>();
                    if (ShouldUseSpatialSubsetImport(shapefilePath))
                    {
                        importPaths.Clear();
                        if (!IsSingleSubsetImportEnabled())
                        {
                            logger.WriteLine(
                                $"Large-file import safety mode is ON for '{Path.GetFileName(shapefilePath)}': using scoped subset import paths; chunking only when the filtered subset exceeds one safe import file (set {EnableSingleSubsetImportEnvVar}=1 to always import the filtered subset directly).");

                            if (!TryCreateSpatiallyFilteredImportPaths(
                                     shapefilePath,
                                     sectionExtents,
                                     utmZoneHint,
                                     logger,
                                     out var filteredImportPaths,
                                     out var chunkTempFolders,
                                     out var subsetKeptCount,
                                     out var subsetTotalCount,
                                     out var noSubsetRecordsInScope,
                                     out var usedDirectScopedSubsetImport,
                                     out var chunkFailureReason))
                            {
                                if (noSubsetRecordsInScope)
                                {
                                    logger.WriteLine(
                                        $"Spatially filtered chunk prep found no raw-coordinate intersections for '{Path.GetFileName(shapefilePath)}'; falling back to full-source chunked import.");
                                }
                                else
                                {
                                    logger.WriteLine(
                                        $"Spatially filtered chunk prep failed for '{Path.GetFileName(shapefilePath)}': {chunkFailureReason}");
                                }

                                if (!TryCreateChunkedSubsetShapefiles(
                                        shapefilePath,
                                        logger,
                                        out var chunkPaths,
                                        out var chunkRootFolder,
                                        out chunkFailureReason))
                                {
                                    logger.WriteLine(
                                        $"Chunked safe import preparation failed for '{Path.GetFileName(shapefilePath)}': {chunkFailureReason}");
                                    summary.ImportFailures++;
                                    continue;
                                }

                                importPaths.AddRange(chunkPaths);
                                if (!string.IsNullOrWhiteSpace(chunkRootFolder))
                                {
                                    temporaryImportFolders.Add(chunkRootFolder);
                                }

                                logger.WriteLine(
                                    $"Chunked safe import enabled for '{Path.GetFileName(shapefilePath)}': {chunkPaths.Count} chunk(s).");
                            }
                            else
                            {
                                importPaths.AddRange(filteredImportPaths);
                                temporaryImportFolders.AddRange(chunkTempFolders);
                                if (usedDirectScopedSubsetImport)
                                {
                                    logger.WriteLine(
                                        $"Scoped direct subset import selected for '{Path.GetFileName(shapefilePath)}': kept {subsetKeptCount}/{subsetTotalCount} record(s), within single-file safe threshold {GetLargeImportChunkRecordCount()}.");
                                }
                                else
                                {
                                    logger.WriteLine(
                                        $"Chunked safe import scope filter for '{Path.GetFileName(shapefilePath)}': kept {subsetKeptCount}/{subsetTotalCount} record(s) before chunking.");
                                    logger.WriteLine(
                                        $"Chunked safe import enabled for '{Path.GetFileName(shapefilePath)}': {filteredImportPaths.Count} chunk(s).");
                                }
                            }
                        }
                        else if (TryCreateSpatialSubsetShapefile(
                                     shapefilePath,
                                     sectionExtents,
                                     utmZoneHint,
                                     logger,
                                     out var subsetPath,
                                     out var subsetKeptCount,
                                     out var subsetTotalCount,
                                     out var noSubsetRecordsInScope))
                        {
                            importPaths.Add(subsetPath);
                            var subsetFolder = Path.GetDirectoryName(subsetPath);
                            if (!string.IsNullOrWhiteSpace(subsetFolder))
                            {
                                temporaryImportFolders.Add(subsetFolder);
                            }

                            logger.WriteLine(
                                $"Spatial subset import enabled for '{Path.GetFileName(shapefilePath)}': kept {subsetKeptCount}/{subsetTotalCount} record(s).");
                        }
                        else if (noSubsetRecordsInScope)
                        {
                            logger.WriteLine(
                                $"Spatial subset import found no raw-coordinate intersections for '{Path.GetFileName(shapefilePath)}'; falling back to chunked safe import.");
                            if (!TryCreateChunkedSubsetShapefiles(
                                    shapefilePath,
                                    logger,
                                    out var chunkPaths,
                                    out var chunkRootFolder,
                                    out var chunkFailureReason))
                            {
                                logger.WriteLine(
                                    $"Chunked safe import preparation failed for '{Path.GetFileName(shapefilePath)}': {chunkFailureReason}");
                                summary.ImportFailures++;
                                continue;
                            }

                            importPaths.AddRange(chunkPaths);
                            if (!string.IsNullOrWhiteSpace(chunkRootFolder))
                            {
                                temporaryImportFolders.Add(chunkRootFolder);
                            }

                            logger.WriteLine(
                                $"Chunked safe import enabled for '{Path.GetFileName(shapefilePath)}': {chunkPaths.Count} chunk(s).");
                        }
                        else
                        {
                            logger.WriteLine(
                                $"Spatial subset preparation failed for '{Path.GetFileName(shapefilePath)}'; skipping native source import to avoid host crash.");
                            summary.ImportFailures++;
                            continue;
                        }
                    }

                    try
                    {
                        for (var importIndex = 0; importIndex < importPaths.Count; importIndex++)
                        {
                            var importPath = importPaths[importIndex];
                            // Keep location-window clipping active for generated subset/chunk files too.
                            // Prior runs have been less stable when subset imports bypass this guard.
                            var useLocationWindowForImport = true;

                            if (importPaths.Count > 1)
                            {
                                logger.WriteLine(
                                    $"Starting shapefile import chunk {importIndex + 1}/{importPaths.Count}: {Path.GetFileName(importPath)}");
                            }
                            else
                            {
                                    logger.WriteLine("Starting shapefile import.");
                            }

                            if (!useLocationWindowForImport)
                            {
                                logger.WriteLine(
                                    $"Shapefile import for '{Path.GetFileName(shapefilePath)}' is using prefiltered subset input; location window disabled.");
                            }

                            if (!TryImportShapefile(
                                    importer,
                                    importPath,
                                    shapefilePath,
                                    sectionExtents,
                                    logger,
                                    out var odTableName,
                                    useLocationWindowForImport))
                            {
                                logger.WriteLine(
                                    $"Shapefile import failed for '{Path.GetFileName(shapefilePath)}' ({Path.GetFileName(importPath)}).");
                                summary.ImportFailures++;
                                continue;
                            }

                            // Find newly-created candidates (polylines + mpolygons)
                            var newCandidates = CaptureNewDispositionCandidateIds(database, existingCandidates);
                            existingCandidates.UnionWith(newCandidates);

                            var newPolylines = newCandidates.Where(IsLwPolylineId).ToList();
                            var newMPolygons = newCandidates.Where(IsMPolygonId).ToList();

                            logger.WriteLine($"Post-import candidates: {newPolylines.Count} LWPOLYLINE, {newMPolygons.Count} MPOLYGON.");

                            if (useLocationWindowForImport && newPolylines.Count == 0 && newMPolygons.Count == 0)
                            {
                                logger.WriteLine("No candidates found with location window; retrying import once without location window.");
                                if (TryImportShapefile(importer, importPath, shapefilePath, sectionExtents, logger, out odTableName, false))
                                {
                                    newCandidates = CaptureNewDispositionCandidateIds(database, existingCandidates);
                                    existingCandidates.UnionWith(newCandidates);
                                    newPolylines = newCandidates.Where(IsLwPolylineId).ToList();
                                    newMPolygons = newCandidates.Where(IsMPolygonId).ToList();
                                    logger.WriteLine($"Retry (no location window) candidates: {newPolylines.Count} LWPOLYLINE, {newMPolygons.Count} MPOLYGON.");
                                }
                            }

                            // Fallback: If Map still made MPOLYGON, convert to LWPOLYLINE and erase MPOLYGON.
                            if (newMPolygons.Count > 0)
                            {
                                var converted = new List<ObjectId>();
                                const int batchSize = 40;
                                var batchCount = (newMPolygons.Count + batchSize - 1) / batchSize;
                                var verboseImportLogging = IsVerboseImportLoggingEnabled();
                                if (verboseImportLogging)
                                {
                                    logger.WriteLine($"MPOLYGON conversion start: total={newMPolygons.Count}, batchSize={batchSize}, batches={batchCount}.");
                                }

                                for (var offset = 0; offset < newMPolygons.Count; offset += batchSize)
                                {
                                    var batchIndex = (offset / batchSize) + 1;
                                    var batch = newMPolygons.Skip(offset).Take(batchSize).ToList();
                                    try
                                    {
                                        var convertedBatch = ConvertPolygonEntitiesToPolylines(
                                            database: database,
                                            logger: logger,
                                            polygonEntityIds: batch,
                                            odTableName: odTableName,
                                            sectionExtents: sectionExtents);
                                        converted.AddRange(convertedBatch);
                                        if (verboseImportLogging)
                                        {
                                            logger.WriteLine($"MPOLYGON conversion batch {batchIndex}/{batchCount}: converted={convertedBatch.Count}, source={batch.Count}.");
                                        }
                                    }
                                    catch (System.Exception ex)
                                    {
                                        logger.WriteLine($"MPOLYGON conversion batch {batchIndex}/{batchCount} failed: {ex.Message}");
                                    }
                                }

                                foreach (var mpId in newMPolygons)
                                    existingCandidates.Remove(mpId);

                                existingCandidates.UnionWith(converted);
                                newPolylines.AddRange(converted);

                                logger.WriteLine($"Converted {converted.Count} MPOLYGON to LWPOLYLINE (OD attempted from '{odTableName}').");
                            }

                            logger.WriteLine($"Shapefile import produced {newPolylines.Count} new polyline candidates.");

                            if (newPolylines.Count == 0)
                            {
                                logger.WriteLine("No new LWPOLYLINE candidates detected after import/conversion.");
                                continue;
                            }

                            FilterAndCollect(
                                database,
                                logger,
                                newPolylines,
                                sectionExtents,
                                existingKeys,
                                dispositionPolylines,
                                summary,
                                Path.GetFileName(shapefilePath));
                        }
                    }
                    finally
                    {
                        foreach (var folder in temporaryImportFolders.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            TryDeleteSpatialSubsetFolder(folder, logger);
                        }
                    }
                }
            }
            finally
            {
                try { overallMeter?.Stop(); } catch { }

                // Restore MAPUSEMPOLYGON (comment out if you want it OFF permanently)
                if (mapUseMPolygonChanged && prevMapUseMPolygon != null)
                {
                    TrySetSystemVariable("MAPUSEMPOLYGON", prevMapUseMPolygon, logger, out _);
                }

                if (polyDisplayChanged && prevPolyDisplay != null)
                {
                    TrySetSystemVariable("POLYDISPLAY", prevPolyDisplay, logger, out _);
                }

                // Important: don't dispose importer (Map may own lifetime).
            }

            summary.ImportedDispositions = dispositionPolylines.Count;
            editor.WriteMessage($"\nImported {summary.ImportedDispositions} dispositions from shapefiles.");
            return summary;
        }

        private static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            try
            {
                var left = Path.GetFullPath(first);
                var right = Path.GetFullPath(second);
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool ShouldSkipLargeNativeDispositionImport(
            string shapefilePath,
            Logger logger,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(shapefilePath))
            {
                return false;
            }

            if (!IsLargeDispositionImportSafetyGateEnabled())
            {
                return false;
            }

            if (IsLargeDispositionImportOverrideEnabled())
            {
                return false;
            }

            var shpBytes = GetSafeFileLengthBytes(shapefilePath);
            var dbfPath = Path.ChangeExtension(shapefilePath, ".dbf");
            var dbfBytes = GetSafeFileLengthBytes(dbfPath);

            if (dbfBytes <= MaxNativeImporterDbfBytesWithoutOverride &&
                shpBytes <= MaxNativeImporterShpBytesWithoutOverride)
            {
                return false;
            }

            reason =
                $"Shapefile import skipped for '{Path.GetFileName(shapefilePath)}': " +
                $"size exceeds safe native-import threshold " +
                $"(SHP={FormatSizeMb(shpBytes)}, DBF={FormatSizeMb(dbfBytes)}, " +
                $"maxSHP={FormatSizeMb(MaxNativeImporterShpBytesWithoutOverride)}, " +
                $"maxDBF={FormatSizeMb(MaxNativeImporterDbfBytesWithoutOverride)}). " +
                "Set ATSBUILD_ALLOW_LARGE_DISPOSITION_IMPORT=1 to force native import " +
                "(when ATSBUILD_SKIP_LARGE_DISPOSITION_IMPORT=1 is enabled).";
            return true;
        }

        private static bool IsLargeDispositionImportSafetyGateEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("ATSBUILD_SKIP_LARGE_DISPOSITION_IMPORT");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var normalized = raw.Trim();
            return string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLargeDispositionImportOverrideEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("ATSBUILD_ALLOW_LARGE_DISPOSITION_IMPORT");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var normalized = raw.Trim();
            return string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static long GetSafeFileLengthBytes(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return 0;
                }

                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatSizeMb(long bytes)
        {
            var mb = bytes / (1024d * 1024d);
            return mb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        private readonly struct ShxEntry
        {
            public ShxEntry(int offsetWords, int contentLengthWords)
            {
                OffsetWords = offsetWords;
                ContentLengthWords = contentLengthWords;
            }

            public int OffsetWords { get; }
            public int ContentLengthWords { get; }
        }

        private readonly struct SpatialSubsetRecord
        {
            public SpatialSubsetRecord(int sourceRecordIndex, int sourceOffsetWords, int contentLengthWords)
            {
                SourceRecordIndex = sourceRecordIndex;
                SourceOffsetWords = sourceOffsetWords;
                ContentLengthWords = contentLengthWords;
            }

            public int SourceRecordIndex { get; }
            public int SourceOffsetWords { get; }
            public int ContentLengthWords { get; }
        }

        private static bool ShouldUseSpatialSubsetImport(string shapefilePath)
        {
            if (string.IsNullOrWhiteSpace(shapefilePath))
            {
                return false;
            }

            var shpBytes = GetSafeFileLengthBytes(shapefilePath);
            var dbfBytes = GetSafeFileLengthBytes(Path.ChangeExtension(shapefilePath, ".dbf"));
            return dbfBytes > MaxNativeImporterDbfBytesWithoutOverride ||
                   shpBytes > MaxNativeImporterShpBytesWithoutOverride;
        }

        private static void TryDeleteSpatialSubsetFolder(string? folderPath, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            try
            {
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, recursive: true);
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine($"Spatial subset cleanup failed for '{folderPath}': {ex.Message}");
            }
        }

        private static bool TryCreateSpatialSubsetShapefile(
            string sourceShapefilePath,
            List<Extents2d> sectionExtents,
            int? utmZoneHint,
            Logger logger,
            out string subsetShapefilePath,
            out int keptRecordCount,
            out int totalRecordCount,
            out bool noSubsetRecordsInScope)
        {
            subsetShapefilePath = string.Empty;
            keptRecordCount = 0;
            totalRecordCount = 0;
            noSubsetRecordsInScope = false;

            if (string.IsNullOrWhiteSpace(sourceShapefilePath) ||
                sectionExtents == null ||
                sectionExtents.Count == 0)
            {
                return false;
            }

            var sourceShxPath = Path.ChangeExtension(sourceShapefilePath, ".shx");
            var sourceDbfPath = Path.ChangeExtension(sourceShapefilePath, ".dbf");
            if (!File.Exists(sourceShapefilePath) || !File.Exists(sourceShxPath) || !File.Exists(sourceDbfPath))
            {
                logger.WriteLine(
                    $"Spatial subset preparation skipped for '{Path.GetFileName(sourceShapefilePath)}': required sidecars are missing.");
                return false;
            }

            if (!TryReadDbfHeader(sourceDbfPath, out var dbfHeaderBytes, out var dbfHeaderLength, out var dbfRecordLength, out var dbfRecordCount, out var dbfHeaderFailure))
            {
                logger.WriteLine(
                    $"Spatial subset preparation failed for '{Path.GetFileName(sourceShapefilePath)}': invalid DBF ({dbfHeaderFailure}).");
                return false;
            }

            var shxEntries = ReadShxEntries(sourceShxPath, logger);
            if (shxEntries.Count == 0)
            {
                logger.WriteLine(
                    $"Spatial subset preparation failed for '{Path.GetFileName(sourceShapefilePath)}': no SHX records.");
                return false;
            }

            totalRecordCount = Math.Min(shxEntries.Count, dbfRecordCount);
            if (totalRecordCount <= 0)
            {
                noSubsetRecordsInScope = true;
                logger.WriteLine(
                    $"Spatial subset preparation skipped for '{Path.GetFileName(sourceShapefilePath)}': source records are empty.");
                return false;
            }

            var subsetSectionExtents = ResolveSectionExtentsForShapefileCoordinates(
                sourceShapefilePath,
                sectionExtents,
                utmZoneHint,
                logger);
            var keptRecords = new List<SpatialSubsetRecord>();
            var loggedUnsupportedShapeTypes = new HashSet<int>();
            var hasBounds = false;
            var subsetBounds = default(Extents2d);

            try
            {
                using (var shpStream = new FileStream(sourceShapefilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var shpReader = new BinaryReader(shpStream, Encoding.UTF8, leaveOpen: true))
                {
                    for (var recordIndex = 0; recordIndex < totalRecordCount; recordIndex++)
                    {
                        var entry = shxEntries[recordIndex];
                        if (entry.OffsetWords <= 0 || entry.ContentLengthWords <= 0)
                        {
                            continue;
                        }

                        var recordOffsetBytes = (long)entry.OffsetWords * 2L;
                        var recordLengthBytes = 8L + ((long)entry.ContentLengthWords * 2L);
                        if (recordOffsetBytes < 100 || recordOffsetBytes + recordLengthBytes > shpStream.Length)
                        {
                            continue;
                        }

                        var hasRecordBounds = TryReadShapeRecordBounds(
                            shpReader,
                            recordOffsetBytes,
                            entry.ContentLengthWords,
                            out var recordBounds,
                            out var shapeType);

                        bool keepRecord;
                        if (hasRecordBounds)
                        {
                            keepRecord = IsWithinSections(recordBounds, subsetSectionExtents);
                        }
                        else if (shapeType == 0)
                        {
                            keepRecord = false;
                        }
                        else
                        {
                            // Keep unsupported non-null shape types rather than risk silently dropping valid records.
                            keepRecord = true;
                            if (loggedUnsupportedShapeTypes.Add(shapeType))
                            {
                                logger.WriteLine(
                                    $"Spatial subset: unsupported shape type {shapeType} encountered; keeping these records unfiltered.");
                            }
                        }

                        if (!keepRecord)
                        {
                            continue;
                        }

                        keptRecords.Add(new SpatialSubsetRecord(
                            sourceRecordIndex: recordIndex,
                            sourceOffsetWords: entry.OffsetWords,
                            contentLengthWords: entry.ContentLengthWords));

                        if (hasRecordBounds)
                        {
                            if (!hasBounds)
                            {
                                subsetBounds = recordBounds;
                                hasBounds = true;
                            }
                            else
                            {
                                subsetBounds = UnionExtents(subsetBounds, recordBounds);
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine(
                    $"Spatial subset preparation failed for '{Path.GetFileName(sourceShapefilePath)}' while scanning records: {ex.Message}");
                return false;
            }

            if (keptRecords.Count == 0)
            {
                noSubsetRecordsInScope = true;
                return false;
            }

            var subsetFolder = Path.Combine(
                Path.GetTempPath(),
                "AtsBackgroundBuilder",
                "shape-subsets",
                Guid.NewGuid().ToString("N"));
            var sourceFileName = Path.GetFileName(sourceShapefilePath);
            var subsetShpPath = Path.Combine(subsetFolder, sourceFileName);
            var subsetShxPath = Path.ChangeExtension(subsetShpPath, ".shx");
            var subsetDbfPath = Path.ChangeExtension(subsetShpPath, ".dbf");
            Extents2d? subsetBoundsOrNull = hasBounds ? subsetBounds : (Extents2d?)null;

            try
            {
                Directory.CreateDirectory(subsetFolder);

                if (!TryWriteSpatialSubsetShpAndShx(
                        sourceShapefilePath,
                        sourceShxPath,
                        subsetShpPath,
                        subsetShxPath,
                        keptRecords,
                        subsetBoundsOrNull,
                        out var shpFailure))
                {
                    logger.WriteLine(
                        $"Spatial subset preparation failed for '{sourceFileName}' while writing SHP/SHX: {shpFailure}");
                    TryDeleteSpatialSubsetFolder(subsetFolder, logger);
                    return false;
                }

                if (!TryWriteSpatialSubsetDbf(
                        sourceDbfPath,
                        subsetDbfPath,
                        dbfHeaderBytes,
                        dbfHeaderLength,
                        dbfRecordLength,
                        keptRecords,
                        out var dbfFailure))
                {
                    logger.WriteLine(
                        $"Spatial subset preparation failed for '{sourceFileName}' while writing DBF: {dbfFailure}");
                    TryDeleteSpatialSubsetFolder(subsetFolder, logger);
                    return false;
                }

                TryCopySpatialSubsetSidecar(sourceShapefilePath, subsetShpPath, ".prj", logger);
                TryCopySpatialSubsetSidecar(sourceShapefilePath, subsetShpPath, ".cpg", logger);
            }
            catch (System.Exception ex)
            {
                logger.WriteLine(
                    $"Spatial subset preparation failed for '{sourceFileName}': {ex.Message}");
                TryDeleteSpatialSubsetFolder(subsetFolder, logger);
                return false;
            }

            subsetShapefilePath = subsetShpPath;
            keptRecordCount = keptRecords.Count;
            return true;
        }

        private static bool TryCreateChunkedSubsetShapefiles(
            string sourceShapefilePath,
            Logger logger,
            out List<string> chunkShapefilePaths,
            out string chunkRootFolder,
            out string failureReason)
        {
            chunkShapefilePaths = new List<string>();
            chunkRootFolder = string.Empty;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(sourceShapefilePath))
            {
                failureReason = "Source shapefile path is empty.";
                return false;
            }

            var sourceShxPath = Path.ChangeExtension(sourceShapefilePath, ".shx");
            var sourceDbfPath = Path.ChangeExtension(sourceShapefilePath, ".dbf");
            if (!File.Exists(sourceShapefilePath) || !File.Exists(sourceShxPath) || !File.Exists(sourceDbfPath))
            {
                failureReason = "Source shapefile sidecars are missing.";
                return false;
            }

            if (!TryReadDbfHeader(
                    sourceDbfPath,
                    out var dbfHeaderBytes,
                    out var dbfHeaderLength,
                    out var dbfRecordLength,
                    out var dbfRecordCount,
                    out var dbfHeaderFailure))
            {
                failureReason = "Invalid DBF for chunked import: " + dbfHeaderFailure;
                return false;
            }

            var shxEntries = ReadShxEntries(sourceShxPath, logger);
            if (shxEntries.Count == 0)
            {
                failureReason = "No SHX entries available for chunked import.";
                return false;
            }

            var totalRecordCount = Math.Min(shxEntries.Count, dbfRecordCount);
            if (totalRecordCount <= 0)
            {
                failureReason = "No records available for chunked import.";
                return false;
            }

            var chunkSize = GetLargeImportChunkRecordCount();
            chunkRootFolder = Path.Combine(
                Path.GetTempPath(),
                "AtsBackgroundBuilder",
                "shape-chunks",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(chunkRootFolder);

            var sourceBaseName = Path.GetFileNameWithoutExtension(sourceShapefilePath) ?? "shape";
            var chunkIndex = 0;
            for (var start = 0; start < totalRecordCount; start += chunkSize)
            {
                var count = Math.Min(chunkSize, totalRecordCount - start);
                if (count <= 0)
                {
                    break;
                }

                var records = new List<SpatialSubsetRecord>(count);
                for (var offset = 0; offset < count; offset++)
                {
                    var recordIndex = start + offset;
                    var entry = shxEntries[recordIndex];
                    if (entry.OffsetWords <= 0 || entry.ContentLengthWords <= 0)
                    {
                        continue;
                    }

                    records.Add(new SpatialSubsetRecord(
                        sourceRecordIndex: recordIndex,
                        sourceOffsetWords: entry.OffsetWords,
                        contentLengthWords: entry.ContentLengthWords));
                }

                if (records.Count == 0)
                {
                    continue;
                }

                chunkIndex++;
                var chunkFolder = Path.Combine(chunkRootFolder, $"chunk-{chunkIndex:0000}");
                Directory.CreateDirectory(chunkFolder);
                var chunkName = $"{sourceBaseName}-chunk-{chunkIndex:0000}.shp";
                var chunkShpPath = Path.Combine(chunkFolder, chunkName);
                var chunkShxPath = Path.ChangeExtension(chunkShpPath, ".shx");
                var chunkDbfPath = Path.ChangeExtension(chunkShpPath, ".dbf");

                if (!TryWriteSpatialSubsetShpAndShx(
                        sourceShapefilePath,
                        sourceShxPath,
                        chunkShpPath,
                        chunkShxPath,
                        records,
                        subsetBounds: null,
                        out var shpFailure))
                {
                    failureReason = $"Chunk {chunkIndex} SHP/SHX write failed: {shpFailure}";
                    TryDeleteSpatialSubsetFolder(chunkRootFolder, logger);
                    chunkRootFolder = string.Empty;
                    chunkShapefilePaths.Clear();
                    return false;
                }

                if (!TryWriteSpatialSubsetDbf(
                        sourceDbfPath,
                        chunkDbfPath,
                        dbfHeaderBytes,
                        dbfHeaderLength,
                        dbfRecordLength,
                        records,
                        out var dbfFailure))
                {
                    failureReason = $"Chunk {chunkIndex} DBF write failed: {dbfFailure}";
                    TryDeleteSpatialSubsetFolder(chunkRootFolder, logger);
                    chunkRootFolder = string.Empty;
                    chunkShapefilePaths.Clear();
                    return false;
                }

                TryCopySpatialSubsetSidecar(sourceShapefilePath, chunkShpPath, ".prj", logger);
                TryCopySpatialSubsetSidecar(sourceShapefilePath, chunkShpPath, ".cpg", logger);
                chunkShapefilePaths.Add(chunkShpPath);
            }

            if (chunkShapefilePaths.Count == 0)
            {
                failureReason = "Chunked import created no chunk files.";
                TryDeleteSpatialSubsetFolder(chunkRootFolder, logger);
                chunkRootFolder = string.Empty;
                return false;
            }

            return true;
        }

        private static bool TryCreateSpatiallyFilteredImportPaths(
            string sourceShapefilePath,
            List<Extents2d> sectionExtents,
            int? utmZoneHint,
            Logger logger,
            out List<string> importPaths,
            out List<string> temporaryFolders,
            out int keptRecordCount,
            out int totalRecordCount,
            out bool noSubsetRecordsInScope,
            out bool usedDirectSubsetImport,
            out string failureReason)
        {
            importPaths = new List<string>();
            temporaryFolders = new List<string>();
            keptRecordCount = 0;
            totalRecordCount = 0;
            noSubsetRecordsInScope = false;
            usedDirectSubsetImport = false;
            failureReason = string.Empty;

            if (!TryCreateSpatialSubsetShapefile(
                    sourceShapefilePath,
                    sectionExtents,
                    utmZoneHint,
                    logger,
                    out var subsetPath,
                    out keptRecordCount,
                    out totalRecordCount,
                    out noSubsetRecordsInScope))
            {
                failureReason = noSubsetRecordsInScope
                    ? "No records intersect requested section extents."
                    : "Spatial subset preparation failed.";
                return false;
            }

            var subsetFolder = Path.GetDirectoryName(subsetPath);
            if (!string.IsNullOrWhiteSpace(subsetFolder))
            {
                temporaryFolders.Add(subsetFolder);
            }

            if (ShouldPreferDirectScopedSubsetImport(keptRecordCount))
            {
                importPaths.Add(subsetPath);
                usedDirectSubsetImport = true;
                return true;
            }

            if (!TryCreateChunkedSubsetShapefiles(
                    subsetPath,
                    logger,
                    out var chunkShapefilePaths,
                    out var chunkRootFolder,
                    out failureReason))
            {
                if (!string.IsNullOrWhiteSpace(subsetFolder))
                {
                    TryDeleteSpatialSubsetFolder(subsetFolder, logger);
                }

                temporaryFolders.Clear();
                importPaths.Clear();
                return false;
            }

            if (!string.IsNullOrWhiteSpace(chunkRootFolder))
            {
                temporaryFolders.Add(chunkRootFolder);
            }

            importPaths.AddRange(chunkShapefilePaths);
            return true;
        }

        private static bool ShouldPreferDirectScopedSubsetImport(int keptRecordCount)
        {
            if (keptRecordCount <= 0)
            {
                return false;
            }

            if (IsSingleSubsetImportEnabled())
            {
                return true;
            }

            return keptRecordCount <= GetLargeImportChunkRecordCount();
        }

        private static List<Extents2d> ResolveSectionExtentsForShapefileCoordinates(
            string sourceShapefilePath,
            List<Extents2d> sectionExtents,
            int? utmZoneHint,
            Logger logger)
        {
            if (sectionExtents.Count == 0)
            {
                return sectionExtents;
            }

            if (!TryReadShapefileHeaderBounds(sourceShapefilePath, out var sourceBounds))
            {
                return sectionExtents;
            }

            if (!LooksGeographicCoordinateSystem(sourceBounds) ||
                !LooksProjectedCoordinateSystem(sectionExtents))
            {
                return sectionExtents;
            }

            if (!utmZoneHint.HasValue || utmZoneHint.Value < 1 || utmZoneHint.Value > 60)
            {
                logger.WriteLine(
                    $"Spatial subset CRS transform skipped for '{Path.GetFileName(sourceShapefilePath)}': UTM zone hint unavailable.");
                return sectionExtents;
            }

            if (TryConvertSectionExtentsFromUtmToGeographic(sectionExtents, utmZoneHint.Value, out var geographicSectionExtents))
            {
                logger.WriteLine(
                    $"Spatial subset CRS transform applied for '{Path.GetFileName(sourceShapefilePath)}': UTM zone {utmZoneHint.Value} -> geographic.");
                return geographicSectionExtents;
            }

            logger.WriteLine(
                $"Spatial subset CRS transform failed for '{Path.GetFileName(sourceShapefilePath)}'; using drawing-coordinate extents.");
            return sectionExtents;
        }

        private static bool TryReadShapefileHeaderBounds(string shapefilePath, out Extents2d bounds)
        {
            bounds = default;

            try
            {
                using (var stream = new FileStream(shapefilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    if (stream.Length < 100)
                    {
                        return false;
                    }

                    stream.Seek(36, SeekOrigin.Begin);
                    var minX = reader.ReadDouble();
                    var minY = reader.ReadDouble();
                    var maxX = reader.ReadDouble();
                    var maxY = reader.ReadDouble();

                    if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY) ||
                        double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
                    {
                        return false;
                    }

                    if (maxX < minX)
                    {
                        (minX, maxX) = (maxX, minX);
                    }

                    if (maxY < minY)
                    {
                        (minY, maxY) = (maxY, minY);
                    }

                    bounds = new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksGeographicCoordinateSystem(Extents2d bounds)
        {
            return bounds.MinPoint.X >= -180.0 && bounds.MaxPoint.X <= 180.0 &&
                   bounds.MinPoint.Y >= -90.0 && bounds.MaxPoint.Y <= 90.0;
        }

        private static bool LooksProjectedCoordinateSystem(List<Extents2d> extents)
        {
            var maxAbs = 0.0;
            foreach (var extent in extents)
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(extent.MinPoint.X));
                maxAbs = Math.Max(maxAbs, Math.Abs(extent.MaxPoint.X));
                maxAbs = Math.Max(maxAbs, Math.Abs(extent.MinPoint.Y));
                maxAbs = Math.Max(maxAbs, Math.Abs(extent.MaxPoint.Y));
            }

            return maxAbs > 1000.0;
        }

        private static bool TryConvertSectionExtentsFromUtmToGeographic(
            List<Extents2d> sourceExtents,
            int zone,
            out List<Extents2d> converted)
        {
            converted = new List<Extents2d>(sourceExtents.Count);
            foreach (var extent in sourceExtents)
            {
                if (!TryConvertUtmExtentToGeographic(extent, zone, out var convertedExtent))
                {
                    converted.Clear();
                    return false;
                }

                converted.Add(convertedExtent);
            }

            return converted.Count > 0;
        }

        private static bool TryConvertUtmExtentToGeographic(Extents2d utmExtent, int zone, out Extents2d geographicExtent)
        {
            geographicExtent = default;

            var corners = new[]
            {
                new Point2d(utmExtent.MinPoint.X, utmExtent.MinPoint.Y),
                new Point2d(utmExtent.MinPoint.X, utmExtent.MaxPoint.Y),
                new Point2d(utmExtent.MaxPoint.X, utmExtent.MinPoint.Y),
                new Point2d(utmExtent.MaxPoint.X, utmExtent.MaxPoint.Y)
            };

            var hasPoint = false;
            var minLon = 0.0;
            var minLat = 0.0;
            var maxLon = 0.0;
            var maxLat = 0.0;

            foreach (var corner in corners)
            {
                if (!TryConvertUtmPointToLonLat(corner.X, corner.Y, zone, out var lon, out var lat))
                {
                    return false;
                }

                if (!hasPoint)
                {
                    minLon = maxLon = lon;
                    minLat = maxLat = lat;
                    hasPoint = true;
                }
                else
                {
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            if (!hasPoint)
            {
                return false;
            }

            geographicExtent = new Extents2d(
                new Point2d(minLon, minLat),
                new Point2d(maxLon, maxLat));
            return true;
        }

        private static bool TryConvertUtmPointToLonLat(double easting, double northing, int zone, out double lonDeg, out double latDeg)
        {
            lonDeg = 0.0;
            latDeg = 0.0;

            if (zone < 1 || zone > 60 ||
                double.IsNaN(easting) || double.IsNaN(northing) ||
                double.IsInfinity(easting) || double.IsInfinity(northing))
            {
                return false;
            }

            // WGS84/NAD83 ellipsoid (difference is negligible at this filtering stage).
            const double a = 6378137.0;
            const double f = 1.0 / 298.257223563;
            const double k0 = 0.9996;

            var e2 = f * (2.0 - f);
            var ePrime2 = e2 / (1.0 - e2);
            var x = easting - 500000.0;
            var y = northing; // Alberta input is northern hemisphere.

            var m = y / k0;
            var mu = m / (a * (1.0 - e2 / 4.0 - 3.0 * e2 * e2 / 64.0 - 5.0 * e2 * e2 * e2 / 256.0));

            var sqrtOneMinusE2 = Math.Sqrt(1.0 - e2);
            var e1 = (1.0 - sqrtOneMinusE2) / (1.0 + sqrtOneMinusE2);
            var j1 = 3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3.0) / 32.0;
            var j2 = 21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4.0) / 32.0;
            var j3 = 151.0 * Math.Pow(e1, 3.0) / 96.0;
            var j4 = 1097.0 * Math.Pow(e1, 4.0) / 512.0;

            var fp = mu +
                     j1 * Math.Sin(2.0 * mu) +
                     j2 * Math.Sin(4.0 * mu) +
                     j3 * Math.Sin(6.0 * mu) +
                     j4 * Math.Sin(8.0 * mu);

            var sinFp = Math.Sin(fp);
            var cosFp = Math.Cos(fp);
            var tanFp = Math.Tan(fp);

            var c1 = ePrime2 * cosFp * cosFp;
            var t1 = tanFp * tanFp;
            var n1 = a / Math.Sqrt(1.0 - e2 * sinFp * sinFp);
            var r1 = a * (1.0 - e2) / Math.Pow(1.0 - e2 * sinFp * sinFp, 1.5);
            var d = x / (n1 * k0);

            var latRad = fp - (n1 * tanFp / r1) *
                         (d * d / 2.0 -
                          (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * ePrime2) * Math.Pow(d, 4.0) / 24.0 +
                          (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * ePrime2 - 3.0 * c1 * c1) * Math.Pow(d, 6.0) / 720.0);

            var lonRad = (d -
                          (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3.0) / 6.0 +
                          (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * ePrime2 + 24.0 * t1 * t1) * Math.Pow(d, 5.0) / 120.0) / cosFp;

            var lonOriginDeg = (zone - 1) * 6.0 - 180.0 + 3.0;
            latDeg = latRad * 180.0 / Math.PI;
            lonDeg = lonOriginDeg + lonRad * 180.0 / Math.PI;

            return !double.IsNaN(lonDeg) && !double.IsNaN(latDeg) &&
                   !double.IsInfinity(lonDeg) && !double.IsInfinity(latDeg);
        }

        private static int GetLargeImportChunkRecordCount()
        {
            var raw = Environment.GetEnvironmentVariable("ATSBUILD_LARGE_IMPORT_CHUNK_RECORDS");
            if (!int.TryParse(raw, out var parsed))
            {
                return DefaultLargeImportChunkRecordCount;
            }

            if (parsed < MinLargeImportChunkRecordCount)
            {
                return MinLargeImportChunkRecordCount;
            }

            if (parsed > MaxLargeImportChunkRecordCount)
            {
                return MaxLargeImportChunkRecordCount;
            }

            return parsed;
        }

        private static bool IsSingleSubsetImportEnabled()
        {
            var raw = Environment.GetEnvironmentVariable(EnableSingleSubsetImportEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var normalized = raw.Trim();
            return string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadDbfHeader(
            string dbfPath,
            out byte[] headerBytes,
            out int headerLength,
            out int recordLength,
            out int recordCount,
            out string failureReason)
        {
            headerBytes = Array.Empty<byte>();
            headerLength = 0;
            recordLength = 0;
            recordCount = 0;
            failureReason = string.Empty;

            try
            {
                using (var dbfStream = new FileStream(dbfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dbfReader = new BinaryReader(dbfStream, Encoding.UTF8, leaveOpen: true))
                {
                    if (dbfStream.Length < 32)
                    {
                        failureReason = "DBF header shorter than 32 bytes.";
                        return false;
                    }

                    var first32 = dbfReader.ReadBytes(32);
                    if (first32.Length != 32)
                    {
                        failureReason = "Unable to read first DBF header bytes.";
                        return false;
                    }

                    recordCount =
                        first32[4] |
                        (first32[5] << 8) |
                        (first32[6] << 16) |
                        (first32[7] << 24);
                    headerLength = first32[8] | (first32[9] << 8);
                    recordLength = first32[10] | (first32[11] << 8);

                    if (recordCount < 0)
                    {
                        failureReason = $"Invalid DBF record count {recordCount}.";
                        return false;
                    }

                    if (headerLength < 32)
                    {
                        failureReason = $"Invalid DBF header length {headerLength}.";
                        return false;
                    }

                    if (recordLength <= 0)
                    {
                        failureReason = $"Invalid DBF record length {recordLength}.";
                        return false;
                    }

                    if (dbfStream.Length < headerLength)
                    {
                        failureReason = $"DBF file shorter than header length ({dbfStream.Length} < {headerLength}).";
                        return false;
                    }

                    dbfStream.Seek(0, SeekOrigin.Begin);
                    headerBytes = dbfReader.ReadBytes(headerLength);
                    if (headerBytes.Length != headerLength)
                    {
                        failureReason = "Unable to read full DBF header.";
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

        private static List<ShxEntry> ReadShxEntries(string shxPath, Logger logger)
        {
            var entries = new List<ShxEntry>();

            try
            {
                using (var shxStream = new FileStream(shxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var shxReader = new BinaryReader(shxStream, Encoding.UTF8, leaveOpen: true))
                {
                    if (shxStream.Length < 100)
                    {
                        logger.WriteLine($"SHX index read failed for '{Path.GetFileName(shxPath)}': header shorter than 100 bytes.");
                        return entries;
                    }

                    shxStream.Seek(100, SeekOrigin.Begin);
                    var recordCount = (int)((shxStream.Length - 100) / 8);
                    for (var i = 0; i < recordCount; i++)
                    {
                        var offsetWords = ReadInt32BigEndian(shxReader);
                        var contentLengthWords = ReadInt32BigEndian(shxReader);
                        entries.Add(new ShxEntry(offsetWords, contentLengthWords));
                    }
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine($"SHX index read failed for '{Path.GetFileName(shxPath)}': {ex.Message}");
            }

            return entries;
        }

        private static bool TryReadShapeRecordBounds(
            BinaryReader shpReader,
            long recordOffsetBytes,
            int contentLengthWords,
            out Extents2d recordBounds,
            out int shapeType)
        {
            recordBounds = default;
            shapeType = 0;

            var contentLengthBytes = (long)contentLengthWords * 2L;
            if (contentLengthBytes < 4)
            {
                return false;
            }

            try
            {
                shpReader.BaseStream.Seek(recordOffsetBytes + 8L, SeekOrigin.Begin);
                shapeType = shpReader.ReadInt32();
                if (shapeType == 0)
                {
                    return false;
                }

                if (IsPointShapeType(shapeType))
                {
                    if (contentLengthBytes < 20)
                    {
                        return false;
                    }

                    var x = shpReader.ReadDouble();
                    var y = shpReader.ReadDouble();
                    if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                    {
                        return false;
                    }

                    var pt = new Point2d(x, y);
                    recordBounds = new Extents2d(pt, pt);
                    return true;
                }

                if (!IsBoxShapeType(shapeType) || contentLengthBytes < 36)
                {
                    return false;
                }

                var minX = shpReader.ReadDouble();
                var minY = shpReader.ReadDouble();
                var maxX = shpReader.ReadDouble();
                var maxY = shpReader.ReadDouble();
                if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY) ||
                    double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
                {
                    return false;
                }

                if (maxX < minX)
                {
                    (minX, maxX) = (maxX, minX);
                }

                if (maxY < minY)
                {
                    (minY, maxY) = (maxY, minY);
                }

                recordBounds = new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPointShapeType(int shapeType)
        {
            return shapeType == 1 || shapeType == 11 || shapeType == 21;
        }

        private static bool IsBoxShapeType(int shapeType)
        {
            switch (shapeType)
            {
                case 3:  // PolyLine
                case 5:  // Polygon
                case 8:  // MultiPoint
                case 13: // PolyLineZ
                case 15: // PolygonZ
                case 18: // MultiPointZ
                case 23: // PolyLineM
                case 25: // PolygonM
                case 28: // MultiPointM
                case 31: // MultiPatch
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryWriteSpatialSubsetShpAndShx(
            string sourceShpPath,
            string sourceShxPath,
            string subsetShpPath,
            string subsetShxPath,
            IReadOnlyList<SpatialSubsetRecord> records,
            Extents2d? subsetBounds,
            out string failureReason)
        {
            failureReason = string.Empty;

            try
            {
                using (var sourceShpStream = new FileStream(sourceShpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sourceShpReader = new BinaryReader(sourceShpStream, Encoding.UTF8, leaveOpen: true))
                using (var sourceShxStream = new FileStream(sourceShxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var shpHeader = new byte[100];
                    var shxHeader = new byte[100];
                    if (sourceShpStream.Read(shpHeader, 0, shpHeader.Length) != shpHeader.Length)
                    {
                        failureReason = "Failed to read source SHP header.";
                        return false;
                    }

                    if (sourceShxStream.Read(shxHeader, 0, shxHeader.Length) != shxHeader.Length)
                    {
                        failureReason = "Failed to read source SHX header.";
                        return false;
                    }

                    using (var subsetShpStream = new FileStream(subsetShpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    using (var subsetShxStream = new FileStream(subsetShxPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    {
                        subsetShpStream.Write(shpHeader, 0, shpHeader.Length);
                        subsetShxStream.Write(shxHeader, 0, shxHeader.Length);

                        var currentOffsetWords = 50; // 100 bytes / 2
                        var targetRecordNumber = 1;
                        foreach (var record in records)
                        {
                            var sourceRecordOffsetBytes = (long)record.SourceOffsetWords * 2L;
                            sourceShpStream.Seek(sourceRecordOffsetBytes, SeekOrigin.Begin);

                            _ = ReadInt32BigEndian(sourceShpReader); // source record number
                            var sourceContentLengthWords = ReadInt32BigEndian(sourceShpReader);
                            if (sourceContentLengthWords <= 0)
                            {
                                failureReason = $"Invalid SHP content length ({sourceContentLengthWords}) at source record {record.SourceRecordIndex + 1}.";
                                return false;
                            }

                            WriteInt32BigEndian(subsetShpStream, targetRecordNumber);
                            WriteInt32BigEndian(subsetShpStream, sourceContentLengthWords);

                            var contentBytes = (long)sourceContentLengthWords * 2L;
                            if (!CopyExactBytes(sourceShpStream, subsetShpStream, contentBytes))
                            {
                                failureReason = $"Failed copying SHP payload for source record {record.SourceRecordIndex + 1}.";
                                return false;
                            }

                            WriteInt32BigEndian(subsetShxStream, currentOffsetWords);
                            WriteInt32BigEndian(subsetShxStream, sourceContentLengthWords);

                            currentOffsetWords += 4 + sourceContentLengthWords; // 8-byte record header + payload
                            targetRecordNumber++;
                        }

                        var subsetShpLengthWords = checked((int)(subsetShpStream.Length / 2L));
                        var subsetShxLengthWords = checked((int)(subsetShxStream.Length / 2L));

                        PatchShapefileHeader(subsetShpStream, shpHeader, subsetShpLengthWords, subsetBounds);
                        PatchShapefileHeader(subsetShxStream, shxHeader, subsetShxLengthWords, subsetBounds);
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

        private static bool TryWriteSpatialSubsetDbf(
            string sourceDbfPath,
            string subsetDbfPath,
            byte[] dbfHeaderBytes,
            int dbfHeaderLength,
            int dbfRecordLength,
            IReadOnlyList<SpatialSubsetRecord> records,
            out string failureReason)
        {
            failureReason = string.Empty;

            try
            {
                using (var sourceDbfStream = new FileStream(sourceDbfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var subsetDbfStream = new FileStream(subsetDbfPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var header = (byte[])dbfHeaderBytes.Clone();
                    WriteInt32LittleEndian(header, 4, records.Count);
                    subsetDbfStream.Write(header, 0, header.Length);

                    var recordBuffer = new byte[dbfRecordLength];
                    foreach (var record in records)
                    {
                        var sourceOffset = (long)dbfHeaderLength + ((long)record.SourceRecordIndex * dbfRecordLength);
                        if (sourceOffset < 0 || sourceOffset + dbfRecordLength > sourceDbfStream.Length)
                        {
                            failureReason = $"DBF source record out of range at index {record.SourceRecordIndex}.";
                            return false;
                        }

                        sourceDbfStream.Seek(sourceOffset, SeekOrigin.Begin);
                        if (!TryReadExact(sourceDbfStream, recordBuffer, dbfRecordLength))
                        {
                            failureReason = $"Failed reading DBF source record at index {record.SourceRecordIndex}.";
                            return false;
                        }

                        subsetDbfStream.Write(recordBuffer, 0, recordBuffer.Length);
                    }

                    subsetDbfStream.WriteByte(0x1A);
                }

                return true;
            }
            catch (System.Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static bool TryReadExact(Stream source, byte[] buffer, int length)
        {
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = source.Read(buffer, totalRead, length - totalRead);
                if (read <= 0)
                {
                    return false;
                }

                totalRead += read;
            }

            return true;
        }

        private static bool CopyExactBytes(Stream source, Stream destination, long totalBytes)
        {
            var buffer = new byte[64 * 1024];
            var remaining = totalBytes;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = source.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    return false;
                }

                destination.Write(buffer, 0, read);
                remaining -= read;
            }

            return true;
        }

        private static void PatchShapefileHeader(
            FileStream targetStream,
            byte[] sourceHeader,
            int fileLengthWords,
            Extents2d? subsetBounds)
        {
            targetStream.Seek(0, SeekOrigin.Begin);
            targetStream.Write(sourceHeader, 0, sourceHeader.Length);
            targetStream.Seek(24, SeekOrigin.Begin);
            WriteInt32BigEndian(targetStream, fileLengthWords);

            if (subsetBounds.HasValue)
            {
                var bounds = subsetBounds.Value;
                targetStream.Seek(36, SeekOrigin.Begin);
                WriteDoubleLittleEndian(targetStream, bounds.MinPoint.X);
                WriteDoubleLittleEndian(targetStream, bounds.MinPoint.Y);
                WriteDoubleLittleEndian(targetStream, bounds.MaxPoint.X);
                WriteDoubleLittleEndian(targetStream, bounds.MaxPoint.Y);
            }

            targetStream.Flush();
        }

        private static void WriteInt32BigEndian(Stream targetStream, int value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            targetStream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteInt32LittleEndian(byte[] target, int offset, int value)
        {
            target[offset + 0] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteDoubleLittleEndian(Stream targetStream, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            targetStream.Write(bytes, 0, bytes.Length);
        }

        private static void TryCopySpatialSubsetSidecar(string sourceShapefilePath, string targetShapefilePath, string extension, Logger logger)
        {
            try
            {
                var sourceSidecar = Path.ChangeExtension(sourceShapefilePath, extension);
                if (!File.Exists(sourceSidecar))
                {
                    return;
                }

                var targetSidecar = Path.ChangeExtension(targetShapefilePath, extension);
                File.Copy(sourceSidecar, targetSidecar, overwrite: true);
            }
            catch (System.Exception ex)
            {
                logger.WriteLine(
                    $"Spatial subset sidecar copy failed for '{Path.GetFileName(sourceShapefilePath)}' ({extension}): {ex.Message}");
            }
        }

        private static Extents2d UnionExtents(Extents2d first, Extents2d second)
        {
            var minX = Math.Min(first.MinPoint.X, second.MinPoint.X);
            var minY = Math.Min(first.MinPoint.Y, second.MinPoint.Y);
            var maxX = Math.Max(first.MaxPoint.X, second.MaxPoint.X);
            var maxY = Math.Max(first.MaxPoint.Y, second.MaxPoint.Y);
            return new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
        }

        private static bool TryGetMap3dImporter(Logger logger, [NotNullWhen(true)] out Importer? importer)
        {
            importer = null;

            try
            {
                importer = HostMapApplicationServices.Application?.Importer;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("Map 3D Importer access failed: " + ex.Message);
            }

            if (importer == null)
            {
                logger.WriteLine("Map 3D Importer not available. Ensure AutoCAD Map 3D is installed/loaded before importing shapefiles.");
                logger.WriteLine("Skipping shapefile import for this run.");
                return false;
            }

            return true;
        }

        private static bool TrySetSystemVariable(string name, object value, Logger logger, out object? previous)
        {
            previous = null;
            try
            {
                previous = AcApp.GetSystemVariable(name);
                AcApp.SetSystemVariable(name, value);
                logger.WriteLine($"{name} set to {value} (previous: {previous ?? "null"})");
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) when (IsIgnoredSystemVariableWriteFailure(name, ex))
            {
                LogIgnoredSystemVariableWriteFailureOnce(name, value, ex, logger);
                return false;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine($"Failed to set system variable '{name}': {ex.Message}");
                return false;
            }
        }

        private static bool IsIgnoredSystemVariableWriteFailure(string name, Autodesk.AutoCAD.Runtime.Exception ex)
        {
            if (!string.Equals(name, "MAPUSEMPOLYGON", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "POLYDISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidInput;
        }

        private static void LogIgnoredSystemVariableWriteFailureOnce(
            string name,
            object value,
            Autodesk.AutoCAD.Runtime.Exception ex,
            Logger logger)
        {
            lock (IgnoredSystemVariableWriteWarningLock)
            {
                if (!IgnoredSystemVariableWriteWarnings.Add(name))
                {
                    return;
                }
            }

            logger.WriteLine(
                $"System variable optimization skipped for '{name}' (value '{value}', status {ex.ErrorStatus}); this AutoCAD build rejected the write, so import will continue without that optimization.");
        }

        private static bool TryImportShapefile(
            Importer importer,
            string importPath,
            string logicalShapefilePath,
            List<Extents2d> sectionExtents,
            Logger logger,
            out string odTableName,
            bool useLocationWindow)
        {
            odTableName = BuildOdTableName(logicalShapefilePath);

            try
            {
                logger.WriteLine($"Importer.Init begin: {Path.GetFileName(importPath)}");
                importer.Init("SHP", importPath);
                logger.WriteLine("Importer.Init completed.");

                // Import window restriction to reduce heavy loads
                var locationWindowApplied = true;
                if (useLocationWindow)
                {
                    locationWindowApplied = TrySetLocationWindow(importer, sectionExtents, logger);
                    var allowNoWindowImport = IsNoLocationWindowImportAllowed();
                    if (!locationWindowApplied)
                    {
                        if (IsCompassSurveyedShapefile(logicalShapefilePath) && !allowNoWindowImport)
                        {
                            logger.WriteLine(
                                $"Shapefile import skipped for '{Path.GetFileName(logicalShapefilePath)}': location window unavailable; refusing unsafe full-file import. Set ATSBUILD_ALLOW_NO_LOCATION_WINDOW_IMPORT=1 to override.");
                            return false;
                        }

                        logger.WriteLine(
                            $"Proceeding without location window for '{Path.GetFileName(logicalShapefilePath)}' (ATSBUILD_ALLOW_NO_LOCATION_WINDOW_IMPORT={(allowNoWindowImport ? "1" : "0")}).");
                    }
                }

                var mappingMode = DetermineDataMappingMode(odTableName, logger);
                var isCompassSurveyShapefile = IsCompassSurveyedShapefile(logicalShapefilePath);
                var verboseImportLogging = IsVerboseImportLoggingEnabled();
                int layerCount = 0;
                var mappingSuccessCount = 0;
                var mappingFailureCount = 0;
                string? firstMappingFailure = null;
                foreach (InputLayer layer in importer)
                {
                    layerCount++;
                    layer.ImportFromInputLayerOn = true;
                    // Ensure DBF attributes become Object Data (OD)
                    var logMappingDetails = !isCompassSurveyShapefile || verboseImportLogging;
                    if (TrySetLayerDataMapping(layer, mappingMode, odTableName, logger, logMappingDetails, out var mappingFailure))
                    {
                        mappingSuccessCount++;
                    }
                    else
                    {
                        mappingFailureCount++;
                        if (string.IsNullOrWhiteSpace(firstMappingFailure) && !string.IsNullOrWhiteSpace(mappingFailure))
                        {
                            firstMappingFailure = mappingFailure;
                        }
                    }
                }

                if (layerCount == 0)
                    logger.WriteLine("Importer.Init succeeded but no input layers were returned.");

                if (isCompassSurveyShapefile && !verboseImportLogging && layerCount > 0)
                {
                    if (mappingFailureCount > 0)
                    {
                        logger.WriteLine(
                            $"Compass data mapping failed for '{Path.GetFileName(logicalShapefilePath)}' on {mappingFailureCount}/{layerCount} layer(s). First='{firstMappingFailure ?? "n/a"}'.");
                    }
                    else
                    {
                        logger.WriteLine(
                            $"Compass data mapping applied for '{Path.GetFileName(logicalShapefilePath)}' on {mappingSuccessCount} layer(s).");
                    }
                }

                // SAFEST: plain Import() (no reflection)
                logger.WriteLine("Importer.Import begin.");
                importer.Import();
                logger.WriteLine("Importer.Import completed.");
                return true;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("Shapefile import failed: " + ex);
                return false;
            }
        }

        private static bool TrySetLocationWindow(Importer importer, List<Extents2d> sectionExtents, Logger logger)
        {
            if (sectionExtents == null || sectionExtents.Count == 0)
            {
                logger.WriteLine("Importer location window skipped: no section extents.");
                return false;
            }

            var union = UnionExtents(sectionExtents);
            var minX = union.MinPoint.X;
            var minY = union.MinPoint.Y;
            var maxX = union.MaxPoint.X;
            var maxY = union.MaxPoint.Y;

            try
            {
                var method = importer.GetType().GetMethod("SetLocationWindowAndOptions");
                if (method == null)
                {
                    logger.WriteLine("Importer location window unsupported: SetLocationWindowAndOptions not found.");
                    return false;
                }

                var ps = method.GetParameters();
                if (ps.Length != 5 || ps[0].ParameterType != typeof(double) || ps[1].ParameterType != typeof(double) ||
                    ps[2].ParameterType != typeof(double) || ps[3].ParameterType != typeof(double) || !ps[4].ParameterType.IsEnum)
                {
                    logger.WriteLine("Importer location window unsupported: unexpected SetLocationWindowAndOptions signature.");
                    return false;
                }

                // LocationOption: usually 2 == kUseLocationWindow
                var option = GetEnumValue(ps[4].ParameterType, 2, "kUseLocationWindow", "UseLocationWindow");

                // Try standard extent ordering first: (xmin, ymin, xmax, ymax).
                try
                {
                    method.Invoke(importer, new object[]
                    {
                        minX,
                        minY,
                        maxX,
                        maxY,
                        option
                    });

                    logger.WriteLine($"Importer location window set (xmin,ymin,xmax,ymax): X[{minX:G},{maxX:G}] Y[{minY:G},{maxY:G}]");
                    return true;
                }
                catch (System.Exception exStandard)
                {
                    var standardMessage = exStandard.InnerException?.Message ?? exStandard.Message;

                    // Fallback for API variants observed in older builds: (xmin, xmax, ymin, ymax).
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

                        logger.WriteLine($"Importer location window set (xmin,xmax,ymin,ymax): X[{minX:G},{maxX:G}] Y[{minY:G},{maxY:G}]");
                        return true;
                    }
                    catch (System.Exception exFallback)
                    {
                        var fallbackMessage = exFallback.InnerException?.Message ?? exFallback.Message;
                        logger.WriteLine($"Importer location window setup failed: standard='{standardMessage}', fallback='{fallbackMessage}'.");
                        return false;
                    }
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("Importer location window setup failed: " + ex.Message);
                return false;
            }
        }

        private static bool IsNoLocationWindowImportAllowed()
        {
            var value = Environment.GetEnvironmentVariable("ATSBUILD_ALLOW_NO_LOCATION_WINDOW_IMPORT");
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static object GetEnumValue(Type enumType, int fallbackNumeric, params string[] names)
        {
            foreach (var name in names)
            {
                try { return Enum.Parse(enumType, name, ignoreCase: true); }
                catch { }
            }

            try { return Enum.ToObject(enumType, fallbackNumeric); }
            catch { return fallbackNumeric; }
        }

        private static ImportDataMapping DetermineDataMappingMode(string odTableName, Logger logger)
        {
            try
            {
                var tables = HostMapApplicationServices.Application.ActiveProject.ODTables;
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
            catch (System.Exception ex)
            {
                logger.WriteLine("OD table lookup failed: " + ex.Message);
            }

            return ImportDataMapping.NewObjectDataOnly;
        }

        private static bool IsCompassSurveyedShapefile(string shapefilePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(shapefilePath) ?? string.Empty;
            return baseName.StartsWith("SURVEYED_POLYGON_N83UTMZ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVerboseImportLoggingEnabled()
        {
            var value = Environment.GetEnvironmentVariable("ATSBUILD_VERBOSE_LOG");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrySetLayerDataMapping(
            InputLayer layer,
            ImportDataMapping preferredMode,
            string preferredOdTableName,
            Logger logger,
            bool logDetails,
            out string? failureSummary)
        {
            failureSummary = null;
            var candidateTables = BuildDataMappingTableCandidates(layer, preferredOdTableName);
            var modeCandidates = BuildDataMappingModeCandidates(preferredMode);
            var attempts = new List<(ImportDataMapping Mode, string TableName)>();
            foreach (var mode in modeCandidates)
            {
                foreach (var tableName in candidateTables)
                {
                    attempts.Add((mode, tableName));
                }
            }

            string? firstError = null;
            string? lastError = null;
            foreach (var attempt in attempts)
            {
                try
                {
                    layer.SetDataMapping(attempt.Mode, attempt.TableName);
                    if (!string.Equals(attempt.TableName, preferredOdTableName, StringComparison.OrdinalIgnoreCase) ||
                        attempt.Mode != preferredMode)
                    {
                        if (logDetails)
                        {
                            logger.WriteLine(
                                $"SetDataMapping fallback succeeded for '{layer.Name}' using mode '{attempt.Mode}' and table '{attempt.TableName}'.");
                        }
                    }

                    return true;
                }
                catch (System.Exception ex)
                {
                    firstError ??= ex.Message;
                    lastError = ex.Message;
                }
            }

            foreach (var mode in modeCandidates)
            {
                if (TrySetLayerDataMappingWithoutTable(layer, mode, out var noTableError))
                {
                    if (logDetails)
                    {
                        logger.WriteLine(
                            $"SetDataMapping fallback succeeded for '{layer.Name}' using mode '{mode}' without explicit table.");
                    }

                    return true;
                }

                if (!string.IsNullOrWhiteSpace(noTableError))
                {
                    firstError ??= noTableError;
                    lastError = noTableError;
                }
            }

            failureSummary = $"First='{firstError ?? "n/a"}', Last='{lastError ?? "n/a"}'";
            if (logDetails)
            {
                logger.WriteLine(
                    $"SetDataMapping failed for '{layer.Name}' after {attempts.Count} attempts (+ no-table fallback). {failureSummary}.");
            }

            return false;
        }

        private static List<ImportDataMapping> BuildDataMappingModeCandidates(ImportDataMapping preferredMode)
        {
            var modes = new List<ImportDataMapping>();
            AddUniqueMappingMode(modes, preferredMode);
            AddUniqueMappingMode(
                modes,
                preferredMode == ImportDataMapping.NewObjectDataOnly
                    ? ImportDataMapping.ExistingObjectDataOnly
                    : ImportDataMapping.NewObjectDataOnly);

            return modes;
        }

        private static void AddUniqueMappingMode(List<ImportDataMapping> modes, ImportDataMapping mode)
        {
            if (!modes.Contains(mode))
            {
                modes.Add(mode);
            }
        }

        private static List<string> BuildDataMappingTableCandidates(InputLayer layer, string preferredOdTableName)
        {
            var candidates = new List<string>();
            AddUniqueCandidate(candidates, preferredOdTableName);
            AddTruncatedTableNameVariants(candidates, preferredOdTableName);
            if (preferredOdTableName.StartsWith("ATS_", StringComparison.OrdinalIgnoreCase))
            {
                AddUniqueCandidate(candidates, preferredOdTableName.Substring(4));
                AddTruncatedTableNameVariants(candidates, preferredOdTableName.Substring(4));
            }

            var layerName = layer.Name ?? string.Empty;
            var colonIdx = layerName.LastIndexOf(':');
            if (colonIdx >= 0 && colonIdx < layerName.Length - 1)
            {
                layerName = layerName.Substring(colonIdx + 1);
            }

            if (layerName.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
            {
                layerName = Path.GetFileNameWithoutExtension(layerName) ?? layerName;
            }

            if (!string.IsNullOrWhiteSpace(layerName))
            {
                var layerDerived = BuildOdTableName(layerName);
                AddUniqueCandidate(candidates, layerDerived);
                AddTruncatedTableNameVariants(candidates, layerDerived);
                if (layerDerived.StartsWith("ATS_", StringComparison.OrdinalIgnoreCase))
                {
                    AddUniqueCandidate(candidates, layerDerived.Substring(4));
                    AddTruncatedTableNameVariants(candidates, layerDerived.Substring(4));
                }
                AddUniqueCandidate(candidates, layerName);
                AddTruncatedTableNameVariants(candidates, layerName);
            }

            AddUniqueCandidate(candidates, string.Empty);
            return candidates;
        }

        private static void AddUniqueCandidate(List<string> candidates, string tableName)
        {
            if (candidates.Any(c => string.Equals(c, tableName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add(tableName);
        }

        private static void AddTruncatedTableNameVariants(List<string> candidates, string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return;
            }

            // Map 3D OD table-name limits vary by version/provider; try both common limits.
            AddTruncatedTableNameVariant(candidates, tableName, 25);
            AddTruncatedTableNameVariant(candidates, tableName, 31);
        }

        private static void AddTruncatedTableNameVariant(List<string> candidates, string tableName, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(tableName) || maxLength <= 0 || tableName.Length <= maxLength)
            {
                return;
            }

            AddUniqueCandidate(candidates, tableName.Substring(0, maxLength));
        }

        private static bool TrySetLayerDataMappingWithoutTable(
            InputLayer layer,
            ImportDataMapping mode,
            out string? error)
        {
            error = null;
            try
            {
                var method = layer.GetType().GetMethod(
                    "SetDataMapping",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(ImportDataMapping) },
                    modifiers: null);

                if (method == null)
                {
                    error = "SetDataMapping(mode) overload not found.";
                    return false;
                }

                method.Invoke(layer, new object[] { mode });
                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        private static bool TryDeleteObjectDataTable(string tableName, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return false;
            }

            try
            {
                var tables = HostMapApplicationServices.Application?.ActiveProject?.ODTables;
                if (tables == null)
                {
                    return false;
                }

                var names = tables.GetTableNames();
                var exists = false;
                if (names != null)
                {
                    foreach (var nObj in names)
                    {
                        var n = nObj as string ?? nObj?.ToString();
                        if (string.Equals(n, tableName, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (!exists)
                {
                    return false;
                }

                var tablesType = tables.GetType();
                foreach (var methodName in new[] { "Remove", "Delete", "RemoveTable" })
                {
                    var method = tablesType.GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        binder: null,
                        types: new[] { typeof(string) },
                        modifiers: null);
                    if (method == null)
                    {
                        continue;
                    }

                    method.Invoke(tables, new object[] { tableName });
                    logger.WriteLine($"Deleted existing OD table '{tableName}' using {methodName}(string) prior to remap.");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine($"OD table delete failed for '{tableName}': {ex.Message}");
            }

            return false;
        }

        private static IReadOnlyList<string> BuildShapefileSearchFolders(Config config)
        {
            var folders = new List<string>();
            AddFolder(folders, config.DispositionShapefileFolder);
            AddFolder(folders, config.ShapefileFolder);

            try { AddFolder(folders, new Config().DispositionShapefileFolder); } catch { }
            try { AddFolder(folders, new Config().ShapefileFolder); } catch { }

            AddFolder(folders, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory);
            return folders;
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;

            if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                folders.Add(folder);
        }

        private static string? ResolveShapefilePath(IReadOnlyList<string> folders, string shapefile, Logger logger)
        {
            var checkedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalidCandidates = new List<string>();
            string? validatedPath = null;

            void TryCandidate(string? candidate)
            {
                if (validatedPath != null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(candidate);
                }
                catch (System.Exception ex)
                {
                    invalidCandidates.Add(candidate + " (invalid path: " + ex.Message + ")");
                    return;
                }

                if (!checkedCandidates.Add(fullPath))
                {
                    return;
                }

                if (!File.Exists(fullPath))
                {
                    return;
                }

                if (TryValidateShapefile(fullPath, out var reason))
                {
                    validatedPath = fullPath;
                    return;
                }

                invalidCandidates.Add(fullPath + " (" + reason + ")");
            }

            TryCandidate(shapefile);
            if (!string.IsNullOrWhiteSpace(validatedPath))
            {
                return validatedPath;
            }

            foreach (var folder in folders)
            {
                TryCandidate(Path.Combine(folder, shapefile));
                if (!string.IsNullOrWhiteSpace(validatedPath))
                {
                    return validatedPath;
                }
            }

            var shapefileName = Path.GetFileName(shapefile);
            if (string.IsNullOrWhiteSpace(shapefileName))
            {
                shapefileName = shapefile;
            }

            var fallbackCandidates = new List<string>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                try
                {
                    var matches = Directory.EnumerateFiles(folder, shapefileName, SearchOption.AllDirectories);
                    foreach (var match in matches)
                    {
                        var fullMatch = Path.GetFullPath(match);
                        if (!checkedCandidates.Contains(fullMatch))
                        {
                            fallbackCandidates.Add(fullMatch);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    logger.WriteLine($"Shapefile fallback search failed in '{folder}' for '{shapefileName}': {ex.Message}");
                }
            }

            string? bestFallback = null;
            long bestFallbackTicks = long.MinValue;
            foreach (var candidate in fallbackCandidates)
            {
                checkedCandidates.Add(candidate);
                if (!TryValidateShapefile(candidate, out var reason))
                {
                    invalidCandidates.Add(candidate + " (" + reason + ")");
                    continue;
                }

                var candidateTicks = GetSafeLastWriteTimeUtcTicks(candidate);
                if (bestFallback == null || candidateTicks > bestFallbackTicks)
                {
                    bestFallback = candidate;
                    bestFallbackTicks = candidateTicks;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestFallback))
            {
                logger.WriteLine(
                    $"Using fallback valid shapefile copy for '{shapefileName}': {bestFallback}");
                return bestFallback;
            }

            if (invalidCandidates.Count > 0)
            {
                logger.WriteLine($"No valid shapefile copy found for '{shapefileName}'. Checked invalid candidates: {string.Join("; ", invalidCandidates)}");
            }

            return null;
        }

        private static bool TryValidateShapefile(string shapefilePath, [NotNullWhen(false)] out string? failureReason)
        {
            failureReason = null;

            try
            {
                var basePath = Path.Combine(
                    Path.GetDirectoryName(shapefilePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(shapefilePath) ?? string.Empty);
                var requiredSidecars = new[] { ".shx", ".dbf" };
                foreach (var ext in requiredSidecars)
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

                        _ = ReadInt32BigEndian(shpReader); // record number
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

        private static HashSet<ObjectId> CaptureDispositionCandidateIds(Database database)
        {
            var ids = new HashSet<ObjectId>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (IsLwPolylineId(id) || IsMPolygonId(id))
                        ids.Add(id);
                }

                tr.Commit();
            }

            return ids;
        }

        private static List<ObjectId> CaptureNewDispositionCandidateIds(Database database, HashSet<ObjectId> existing)
        {
            var newIds = new List<ObjectId>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!IsLwPolylineId(id) && !IsMPolygonId(id))
                        continue;

                    if (!existing.Contains(id))
                        newIds.Add(id);
                }

                tr.Commit();
            }

            return newIds;
        }

        private static bool IsLwPolylineId(ObjectId id)
        {
            var dxf = id.ObjectClass?.DxfName;
            return string.Equals(dxf, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMPolygonId(ObjectId id)
        {
            var dxf = id.ObjectClass?.DxfName;
            var cls = id.ObjectClass?.Name;
            if (string.Equals(dxf, "MPOLYGON", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "AcDbMPolygon", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(dxf, "POLYGON", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "AcDbPolygon", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(dxf) &&
                dxf.IndexOf("MPOLYGON", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(cls) &&
                cls.IndexOf("MPOLYGON", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static List<ObjectId> ConvertPolygonEntitiesToPolylines(
            Database database,
            Logger logger,
            IReadOnlyList<ObjectId> polygonEntityIds,
            string odTableName,
            List<Extents2d> sectionExtents)
        {
            var created = new List<ObjectId>();
            if (polygonEntityIds == null || polygonEntityIds.Count == 0)
                return created;

            Autodesk.AutoCAD.Runtime.ProgressMeter? meter = null;
            try
            {
                meter = new Autodesk.AutoCAD.Runtime.ProgressMeter();
                meter.SetLimit(polygonEntityIds.Count);
                meter.Start("ATSBUILD: Converting polygons");
            }
            catch
            {
                meter = null;
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    foreach (var polyId in polygonEntityIds)
                    {
                        try { meter?.MeterProgress(); } catch { }

                        if (!polyId.IsValid)
                            continue;

                        var ent = tr.GetObject(polyId, OpenMode.ForWrite, false) as Entity;
                        if (ent == null)
                            continue;

                        if (!IsEntityWithinSections(ent, sectionExtents))
                        {
                            try { ent.Erase(); } catch { }
                            continue;
                        }

                        var exploded = new DBObjectCollection();
                        Polyline? bestBoundary = null;

                        try
                        {
                            ent.Explode(exploded);
                            bestBoundary = SelectLargestClosedPolyline(exploded);
                        }
                        catch (System.Exception ex)
                        {
                            logger.WriteLine($"Polygon explode failed: {ex.Message}");
                        }

                        try
                        {
                            if (bestBoundary != null)
                            {
                                var newPl = (Polyline)bestBoundary.Clone();
                                CopyBasicEntityProps(ent, newPl);
                                NormalizePolylineDisplay(newPl);

                                ms.AppendEntity(newPl);
                                tr.AddNewlyCreatedDBObject(newPl, true);

                                TryCopyObjectData(polyId, newPl.ObjectId, odTableName, logger);
                                created.Add(newPl.ObjectId);
                            }

                            ent.Erase(); // remove MPOLYGON so POLYDISPLAY isn't needed
                        }
                        catch (System.Exception ex)
                        {
                            logger.WriteLine($"Polygon conversion failed: {ex.Message}");
                        }
                        finally
                        {
                            foreach (DBObject dbo in exploded)
                            {
                                try { dbo.Dispose(); } catch { }
                            }
                        }
                    }

                    tr.Commit();
                }
            }
            finally
            {
                try { meter?.Stop(); } catch { }
            }

            return created;
        }

        private static bool IsEntityWithinSections(Entity ent, List<Extents2d> sectionExtents)
        {
            if (sectionExtents == null || sectionExtents.Count == 0)
                return true;

            Extents3d e3d;
            try { e3d = ent.GeometricExtents; }
            catch { return true; }

            var e2d = new Extents2d(
                new Point2d(e3d.MinPoint.X, e3d.MinPoint.Y),
                new Point2d(e3d.MaxPoint.X, e3d.MaxPoint.Y));

            return IsWithinSections(e2d, sectionExtents);
        }

        private static Polyline? SelectLargestClosedPolyline(DBObjectCollection exploded)
        {
            Polyline? best = null;
            double bestArea = -1;

            foreach (DBObject dbo in exploded)
            {
                if (dbo is not Polyline pl)
                    continue;

                if (!pl.Closed)
                    continue;

                double area;
                try { area = Math.Abs(pl.Area); }
                catch { area = 0; }

                if (area > bestArea)
                {
                    bestArea = area;
                    best = pl;
                }
            }

            return best;
        }

        private static void CopyBasicEntityProps(Entity source, Entity dest)
        {
            try { dest.Layer = source.Layer; } catch { }
            try { dest.Color = source.Color; } catch { }
            try { dest.Linetype = source.Linetype; } catch { }
            try { dest.LinetypeScale = source.LinetypeScale; } catch { }
            try { dest.LineWeight = source.LineWeight; } catch { }
            try { dest.Transparency = source.Transparency; } catch { }
            try { dest.Visible = source.Visible; } catch { }
        }

        private static void NormalizePolylineDisplay(Polyline pl)
        {
            try { pl.ConstantWidth = 0.0; } catch { }

            try
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    pl.SetStartWidthAt(i, 0.0);
                    pl.SetEndWidthAt(i, 0.0);
                }
            }
            catch { }
        }

        private static void TryCopyObjectData(ObjectId sourceId, ObjectId destId, string odTableName, Logger logger)
        {
            try
            {
                var project = HostMapApplicationServices.Application?.ActiveProject;
                if (project == null)
                {
                    return;
                }

                var tables = project.ODTables;
                var names = tables.GetTableNames();
                if (tables == null || names == null)
                {
                    return;
                }

                var candidateTables = new List<string>();
                if (!string.IsNullOrWhiteSpace(odTableName))
                {
                    candidateTables.Add(odTableName);
                }

                foreach (var nObj in names)
                {
                    var n = nObj as string ?? nObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(n) &&
                        !candidateTables.Any(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidateTables.Add(n);
                    }
                }

                foreach (var tableName in candidateTables)
                {
                    Autodesk.Gis.Map.ObjectData.Table table;
                    try
                    {
                        table = tables[tableName];
                    }
                    catch
                    {
                        continue;
                    }

                    using (table)
                    using (OdRecords records = table.GetObjectTableRecords(0, sourceId, MapOpenMode.OpenForRead, true))
                    {
                        if (records == null || records.Count == 0)
                        {
                            continue;
                        }

                        foreach (OdRecord srcRecord in records)
                        {
                            var newRecord = OdRecord.Create();
                            table.InitRecord(newRecord);

                            int fieldCount = Math.Min(srcRecord.Count, newRecord.Count);
                            for (int i = 0; i < fieldCount; i++)
                            {
                                try
                                {
                                    var srcVal = srcRecord[i];
                                    var dstVal = newRecord[i];

                                    switch (srcVal.Type)
                                    {
                                        case MapDataType.Character:
                                            dstVal.Assign(srcVal.StrValue ?? string.Empty);
                                            break;

                                        case MapDataType.Integer:
                                            dstVal.Assign(srcVal.Int32Value);
                                            break;

                                        case MapDataType.Real:
                                            dstVal.Assign(srcVal.DoubleValue);
                                            break;

                                        default:
                                            dstVal.Assign(srcVal.ToString());
                                            break;
                                    }
                                }
                                catch
                                {
                                    // ignore per-field failures
                                }
                            }

                            table.AddRecord(newRecord, destId);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine($"OD copy failed (table '{odTableName}'): {ex.Message}");
            }
        }

        private static List<Extents2d> BuildSectionBufferExtents(Database database, IReadOnlyList<ObjectId> sectionPolylineIds, double buffer)
        {
            var extents = new List<Extents2d>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var id in sectionPolylineIds)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null)
                        continue;

                    var e = pl.GeometricExtents;
                    extents.Add(new Extents2d(
                        new Point2d(e.MinPoint.X - buffer, e.MinPoint.Y - buffer),
                        new Point2d(e.MaxPoint.X + buffer, e.MaxPoint.Y + buffer)));
                }

                tr.Commit();
            }

            return extents;
        }

        private static Extents2d UnionExtents(List<Extents2d> extents)
        {
            var minX = extents[0].MinPoint.X;
            var minY = extents[0].MinPoint.Y;
            var maxX = extents[0].MaxPoint.X;
            var maxY = extents[0].MaxPoint.Y;

            for (int i = 1; i < extents.Count; i++)
            {
                var e = extents[i];
                minX = Math.Min(minX, e.MinPoint.X);
                minY = Math.Min(minY, e.MinPoint.Y);
                maxX = Math.Max(maxX, e.MaxPoint.X);
                maxY = Math.Max(maxY, e.MaxPoint.Y);
            }

            return new Extents2d(new Point2d(minX, minY), new Point2d(maxX, maxY));
        }

        private static void ZoomToSectionExtents(Editor editor, List<Extents2d> sectionExtents, Logger logger)
        {
            if (sectionExtents == null || sectionExtents.Count == 0)
                return;

            try
            {
                var union = UnionExtents(sectionExtents);
                using (var view = editor.GetCurrentView())
                {
                    var min = union.MinPoint;
                    var max = union.MaxPoint;
                    var width = Math.Max(1.0, max.X - min.X);
                    var height = Math.Max(1.0, max.Y - min.Y);
                    const double marginFactor = 1.2;

                    view.CenterPoint = new Point2d((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5);
                    view.Width = width * marginFactor;
                    view.Height = height * marginFactor;

                    editor.SetCurrentView(view);
                }

                logger.WriteLine("Temporarily zoomed view to section extents for shapefile import.");
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("Failed to zoom view to section extents: " + ex.Message);
            }
        }

        private static HashSet<string> BuildExistingFeatureKeys(Database database, Logger logger, List<Extents2d> sectionExtents)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null)
                        continue;

                    try
                    {
                        var ext = pl.GeometricExtents;
                        var e2d = new Extents2d(
                            new Point2d(ext.MinPoint.X, ext.MinPoint.Y),
                            new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y));

                        if (!IsWithinSections(e2d, sectionExtents))
                            continue;
                    }
                    catch
                    {
                        // Ignore malformed extents and continue scanning.
                        continue;
                    }

                    var key = BuildFeatureKey(pl, id, logger);
                    if (!string.IsNullOrWhiteSpace(key))
                        keys.Add(key);
                }

                tr.Commit();
            }

            return keys;
        }

        private static void FilterAndCollect(
            Database database,
            Logger logger,
            List<ObjectId> newIds,
            List<Extents2d> sectionExtents,
            HashSet<string> existingKeys,
            List<ObjectId> dispositionPolylines,
            ShapefileImportSummary summary,
            string shapefileName)
        {
            var filteredStart = summary.FilteredDispositions;
            var dedupedStart = summary.DedupedDispositions;
            var acceptedStart = dispositionPolylines.Count;

            Autodesk.AutoCAD.Runtime.ProgressMeter? meter = null;
            try
            {
                meter = new Autodesk.AutoCAD.Runtime.ProgressMeter();
                meter.SetLimit(newIds.Count);
                meter.Start($"ATSBUILD: Filtering {shapefileName}");
            }
            catch
            {
                meter = null;
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    foreach (var id in newIds)
                    {
                        try { meter?.MeterProgress(); } catch { }

                        var pl = tr.GetObject(id, OpenMode.ForWrite) as Polyline;
                        if (pl == null)
                            continue;

                        NormalizePolylineDisplay(pl);

                        if (!pl.Closed)
                        {
                            summary.FilteredDispositions++;
                            pl.Erase();
                            continue;
                        }

                        Extents3d ext;
                        try
                        {
                            ext = pl.GeometricExtents;
                        }
                        catch (System.Exception ex)
                        {
                            logger.WriteLine($"Filtered polyline {id} due to invalid extents: {ex.Message}");
                            summary.FilteredDispositions++;
                            try { pl.Erase(); } catch { }
                            continue;
                        }

                        var e2d = new Extents2d(
                            new Point2d(ext.MinPoint.X, ext.MinPoint.Y),
                            new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y));

                        if (!IsWithinSections(e2d, sectionExtents))
                        {
                            summary.FilteredDispositions++;
                            pl.Erase();
                            continue;
                        }

                        var key = BuildFeatureKey(pl, id, logger);
                        if (!string.IsNullOrWhiteSpace(key) && existingKeys.Contains(key))
                        {
                            summary.DedupedDispositions++;
                            pl.Erase();
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(key))
                            existingKeys.Add(key);

                        dispositionPolylines.Add(id);
                    }

                    tr.Commit();
                }
            }
            finally
            {
                try { meter?.Stop(); } catch { }
            }

            var accepted = dispositionPolylines.Count - acceptedStart;
            var filtered = summary.FilteredDispositions - filteredStart;
            var deduped = summary.DedupedDispositions - dedupedStart;
            logger.WriteLine($"Shapefile '{shapefileName}' results: accepted {accepted}, filtered {filtered}, deduped {deduped}.");
        }

        private static bool IsWithinSections(Extents2d polyExtents, List<Extents2d> sectionExtents)
        {
            foreach (var sectionExtent in sectionExtents)
            {
                if (GeometryUtils.ExtentsIntersect(polyExtents, sectionExtent))
                    return true;
            }

            return false;
        }

        private static string BuildFeatureKey(Polyline polyline, ObjectId id, Logger logger)
        {
            string roundedCenter;
            try
            {
                var extents = polyline.GeometricExtents;
                var centerX = (extents.MinPoint.X + extents.MaxPoint.X) / 2.0;
                var centerY = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0;
                roundedCenter = $"{Math.Round(centerX, 2):F2},{Math.Round(centerY, 2):F2}";
            }
            catch (System.Exception ex)
            {
                logger.WriteLine($"Feature key fallback for polyline {id}: {ex.Message}");
                roundedCenter = id.ToString();
            }

            var od = OdHelpers.ReadObjectData(id, logger);
            if (od != null)
            {
                od.TryGetValue("DISP_NUM", out var dispNum);
                od.TryGetValue("COMPANY", out var company);
                od.TryGetValue("PURPCD", out var purpose);
                return $"{dispNum}|{company}|{purpose}|{roundedCenter}";
            }

            return roundedCenter;
        }

        private static string BuildOdTableName(string shapefilePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(shapefilePath) ?? "DISP";
            var sb = new StringBuilder();

            foreach (var ch in baseName.Trim())
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

            var name = sb.Length == 0 ? "DISP" : sb.ToString();

            if (char.IsDigit(name[0]))
                name = "ATS_" + name;

            if (!name.StartsWith("ATS_", StringComparison.OrdinalIgnoreCase))
                name = "ATS_" + name;

            const int maxLen = 31;
            if (name.Length > maxLen)
                name = name.Substring(0, maxLen);

            return name;
        }

        private static void LogShapefileSidecars(string shapefilePath, Logger logger)
        {
            var basePath = Path.Combine(
                Path.GetDirectoryName(shapefilePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(shapefilePath));

            foreach (var ext in new[] { ".shp", ".shx", ".dbf" })
            {
                var candidate = basePath + ext;
                if (!File.Exists(candidate))
                    logger.WriteLine($"Missing shapefile sidecar: {candidate}");
            }
        }
    }
}

