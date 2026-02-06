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

namespace AtsBackgroundBuilder
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
        public static ShapefileImportSummary ImportShapefiles(
            Database database,
            Editor editor,
            Logger logger,
            Config config,
            IReadOnlyList<ObjectId> sectionPolylineIds,
            List<ObjectId> dispositionPolylines)
        {
            var summary = new ShapefileImportSummary();

            if (config.DispositionShapefiles == null || config.DispositionShapefiles.Length == 0)
            {
                logger.WriteLine("No disposition shapefiles configured.");
                return summary;
            }

            var sectionExtents = BuildSectionBufferExtents(database, sectionPolylineIds, config.SectionBufferDistance);
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
            logger.WriteLine($"Section extents loaded: {sectionExtents.Count} (buffer {config.SectionBufferDistance}).");

            if (!TryGetMap3dImporter(logger, out var importer))
            {
                summary.ImportFailures += config.DispositionShapefiles.Length;
                return summary;
            }

            // Your suggestion: set MAPUSEMPOLYGON BEFORE import starts.
            // This is the safest way to avoid MPOLYGON + POLYDISPLAY altogether.
            object? prevMapUseMPolygon = null;
            bool mapUseMPolygonChanged = TrySetSystemVariable("MAPUSEMPOLYGON", 0, logger, out prevMapUseMPolygon);

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
                    var shapefilePath = ResolveShapefilePath(searchFolders, shapefile);
                    if (string.IsNullOrWhiteSpace(shapefilePath))
                    {
                        logger.WriteLine($"Shapefile missing: {shapefile}. Searched: {string.Join("; ", searchFolders)}");
                        summary.ImportFailures++;
                        continue;
                    }

                    logger.WriteLine($"Using shapefile: {shapefilePath}");
                    LogShapefileSidecars(shapefilePath, logger);

                    logger.WriteLine("Starting shapefile import.");
                    if (!TryImportShapefile(importer, shapefilePath, sectionExtents, logger, out var odTableName, true))
                    {
                        logger.WriteLine("Shapefile import failed.");
                        summary.ImportFailures++;
                        continue;
                    }

                    // Find newly-created candidates (polylines + mpolygons)
                    var newCandidates = CaptureNewDispositionCandidateIds(database, existingCandidates);
                    existingCandidates.UnionWith(newCandidates);

                    var newPolylines = newCandidates.Where(IsLwPolylineId).ToList();
                    var newMPolygons = newCandidates.Where(IsMPolygonId).ToList();

                    logger.WriteLine($"Post-import candidates: {newPolylines.Count} LWPOLYLINE, {newMPolygons.Count} MPOLYGON.");

                    if (newPolylines.Count == 0 && newMPolygons.Count == 0)
                    {
                        logger.WriteLine("No candidates found with location window; retrying import once without location window.");
                        if (TryImportShapefile(importer, shapefilePath, sectionExtents, logger, out odTableName, false))
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
                        var converted = ConvertPolygonEntitiesToPolylines(
                            database: database,
                            logger: logger,
                            polygonEntityIds: newMPolygons,
                            odTableName: odTableName,
                            sectionExtents: sectionExtents);

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
                try { overallMeter?.Stop(); } catch { }

                // Restore MAPUSEMPOLYGON (comment out if you want it OFF permanently)
                if (mapUseMPolygonChanged && prevMapUseMPolygon != null)
                {
                    TrySetSystemVariable("MAPUSEMPOLYGON", prevMapUseMPolygon, logger, out _);
                }

                // Important: don't dispose importer (Map may own lifetime).
            }

            summary.ImportedDispositions = dispositionPolylines.Count;
            editor.WriteMessage($"\nImported {summary.ImportedDispositions} dispositions from shapefiles.");
            return summary;
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
            catch (System.Exception ex)
            {
                logger.WriteLine($"Failed to set system variable '{name}': {ex.Message}");
                return false;
            }
        }

        private static bool TryImportShapefile(
            Importer importer,
            string shapefilePath,
            List<Extents2d> sectionExtents,
            Logger logger,
            out string odTableName,
            bool useLocationWindow)
        {
            odTableName = BuildOdTableName(shapefilePath);

            try
            {
                importer.Init("SHP", shapefilePath);

                // Import window restriction to reduce heavy loads
                if (useLocationWindow)
                {
                    TrySetLocationWindow(importer, sectionExtents, logger);
                }

                // Ensure DBF attributes become Object Data (OD)
                var mappingMode = DetermineDataMappingMode(odTableName, logger);

                int layerCount = 0;
                foreach (InputLayer layer in importer)
                {
                    layerCount++;
                    layer.ImportFromInputLayerOn = true;

                    try
                    {
                        layer.SetDataMapping(mappingMode, odTableName);
                    }
                    catch (System.Exception ex)
                    {
                        // Try opposite mapping mode as fallback
                        try
                        {
                            var fallbackMode = mappingMode == ImportDataMapping.NewObjectDataOnly
                                ? ImportDataMapping.ExistingObjectDataOnly
                                : ImportDataMapping.NewObjectDataOnly;

                            layer.SetDataMapping(fallbackMode, odTableName);
                            logger.WriteLine($"SetDataMapping fallback succeeded for '{layer.Name}' using mode '{fallbackMode}'.");
                        }
                        catch (System.Exception fallbackEx)
                        {
                            logger.WriteLine($"SetDataMapping failed for '{layer.Name}': {ex.Message} (fallback failed: {fallbackEx.Message})");
                        }
                    }
                }

                if (layerCount == 0)
                    logger.WriteLine("Importer.Init succeeded but no input layers were returned.");

                // SAFEST: plain Import() (no reflection)
                importer.Import();
                return true;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("Shapefile import failed: " + ex);
                return false;
            }
        }

        private static void TrySetLocationWindow(Importer importer, List<Extents2d> sectionExtents, Logger logger)
        {
            if (sectionExtents == null || sectionExtents.Count == 0)
                return;

            var union = UnionExtents(sectionExtents);

            try
            {
                var method = importer.GetType().GetMethod("SetLocationWindowAndOptions");
                if (method == null)
                    return;

                var ps = method.GetParameters();
                if (ps.Length != 5 || ps[0].ParameterType != typeof(double) || ps[1].ParameterType != typeof(double) ||
                    ps[2].ParameterType != typeof(double) || ps[3].ParameterType != typeof(double) || !ps[4].ParameterType.IsEnum)
                    return;

                // LocationOption: usually 2 == kUseLocationWindow
                var option = GetEnumValue(ps[4].ParameterType, 2, "kUseLocationWindow", "UseLocationWindow");

                method.Invoke(importer, new object[]
                {
                    union.MinPoint.X,
                    union.MaxPoint.X,
                    union.MinPoint.Y,
                    union.MaxPoint.Y,
                    option
                });

                logger.WriteLine($"Importer location window set: X[{union.MinPoint.X:G},{union.MaxPoint.X:G}] Y[{union.MinPoint.Y:G},{union.MaxPoint.Y:G}]");
            }
            catch
            {
                // non-critical
            }
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

        private static string? ResolveShapefilePath(IReadOnlyList<string> folders, string shapefile)
        {
            if (File.Exists(shapefile))
                return shapefile;

            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, shapefile);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
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

            return string.Equals(dxf, "MPOLYGON", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(cls, "AcDbMPolygon", StringComparison.OrdinalIgnoreCase);
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
            if (string.IsNullOrWhiteSpace(odTableName))
                return;

            try
            {
                var project = HostMapApplicationServices.Application?.ActiveProject;
                if (project == null)
                    return;

                var tables = project.ODTables;
                var names = tables.GetTableNames();

                // FIX: no .Any() on StringCollection — manual check
                bool exists = false;
                if (names != null)
                {
                    foreach (var nObj in names)
                    {
                        var n = nObj as string ?? nObj?.ToString();
                        if (string.Equals(n, odTableName, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                }

                if (!exists)
                    return;

                var table = tables[odTableName];

                using (OdRecords records = table.GetObjectTableRecords(0, sourceId, MapOpenMode.OpenForRead, true))
                {
                    if (records == null || records.Count == 0)
                        return;

                    foreach (OdRecord srcRecord in records)
                    {
                        var newRecord = OdRecord.Create();
                        table.InitRecord(newRecord);

                        int n = Math.Min(srcRecord.Count, newRecord.Count);
                        for (int i = 0; i < n; i++)
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

                        var ext = pl.GeometricExtents;
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
            var extents = polyline.GeometricExtents;
            var centerX = (extents.MinPoint.X + extents.MaxPoint.X) / 2.0;
            var centerY = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0;
            var roundedCenter = $"{Math.Round(centerX, 2):F2},{Math.Round(centerY, 2):F2}";

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
