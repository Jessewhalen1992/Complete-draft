using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

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
        private static readonly string[] MapImportTypeNames =
        {
            "Autodesk.Gis.Map.ImportExport.MapImport",
            "Autodesk.Gis.Map.Platform.ImportExport.MapImport"
        };

        private static readonly string[] MapImportAssemblyNames =
        {
            "ManagedMapApi",
            "AcMapImportExport",
            "AcMapMgd"
        };

        private static readonly Lazy<Type?> MapImportType = new Lazy<Type?>(ResolveMapImportType);

        public static ShapefileImportSummary ImportShapefiles(
            Database database,
            Editor editor,
            Logger logger,
            Config config,
            IReadOnlyList<ObjectId> sectionPolylineIds,
            List<ObjectId> dispositionPolylines)
        {
            var summary = new ShapefileImportSummary();
            if (config.DispositionShapefiles.Length == 0)
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

            var existingKeys = BuildExistingFeatureKeys(database, logger);
            var existingIds = CapturePolylineIds(database);
            var searchFolders = BuildShapefileSearchFolders(config);
            logger.WriteLine($"Shapefile search folders: {string.Join("; ", searchFolders)}");
            logger.WriteLine($"Section extents loaded: {sectionExtents.Count} (buffer {config.SectionBufferDistance}).");

            if (!TryGetMapImportType(logger, out var mapImportType))
            {
                summary.ImportFailures += config.DispositionShapefiles.Length;
                return summary;
            }

            foreach (var shapefile in config.DispositionShapefiles)
            {
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
                if (!TryImportShapefile(mapImportType, shapefilePath, logger))
                {
                    logger.WriteLine("Shapefile import failed.");
                    summary.ImportFailures++;
                    continue;
                }

                var newIds = CaptureNewPolylineIds(database, existingIds);
                existingIds.UnionWith(newIds);
                logger.WriteLine($"Shapefile import produced {newIds.Count} new polylines.");

                if (newIds.Count == 0)
                {
                    logger.WriteLine("No new LWPOLYLINE entities detected after import.");
                    continue;
                }

                FilterAndCollect(database, logger, newIds, sectionExtents, existingKeys, dispositionPolylines, summary, Path.GetFileName(shapefilePath));
            }

            summary.ImportedDispositions = dispositionPolylines.Count;
            editor.WriteMessage($"\nImported {summary.ImportedDispositions} dispositions from shapefiles.");
            return summary;
        }

        private static bool TryGetMapImportType(Logger logger, out Type mapImportType)
        {
            mapImportType = MapImportType.Value;
            if (mapImportType == null)
            {
                logger.WriteLine("MapImport type not found. Ensure Map 3D is available before importing shapefiles.");
                logger.WriteLine($"Loaded assemblies: {string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name))}");
                logger.WriteLine("Skipping shapefile import for this run.");
                return false;
            }

            return true;
        }

        private static Type? ResolveMapImportType()
        {
            foreach (var typeName in MapImportTypeNames)
            {
                var resolved = Type.GetType(typeName);
                if (resolved != null)
                {
                    return resolved;
                }

                foreach (var assemblyName in MapImportAssemblyNames)
                {
                    resolved = Type.GetType($"{typeName}, {assemblyName}");
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            foreach (var assemblyName in MapImportAssemblyNames)
            {
                TryLoadAssembly(assemblyName);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var typeName in MapImportTypeNames)
                {
                    var resolved = assembly.GetType(typeName, false);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }
            }

            return null;
        }

        private static void TryLoadAssembly(string assemblyName)
        {
            try
            {
                Assembly.Load(assemblyName);
            }
            catch
            {
                // ignored
            }
        }

        private static IReadOnlyList<string> BuildShapefileSearchFolders(Config config)
        {
            var folders = new List<string>();
            AddFolder(folders, config.ShapefileFolder);
            AddFolder(folders, new Config().ShapefileFolder);
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

        private static string? ResolveShapefilePath(IReadOnlyList<string> folders, string shapefile)
        {
            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, shapefile);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static HashSet<ObjectId> CapturePolylineIds(Database database)
        {
            var ids = new HashSet<ObjectId>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.ObjectClass.DxfName != "LWPOLYLINE")
                    {
                        continue;
                    }

                    ids.Add(id);
                }

                transaction.Commit();
            }

            return ids;
        }

        private static List<ObjectId> CaptureNewPolylineIds(Database database, HashSet<ObjectId> existingIds)
        {
            var newIds = new List<ObjectId>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (id.ObjectClass.DxfName != "LWPOLYLINE")
                    {
                        continue;
                    }

                    if (!existingIds.Contains(id))
                    {
                        newIds.Add(id);
                    }
                }

                transaction.Commit();
            }

            return newIds;
        }

        private static List<Extents2d> BuildSectionBufferExtents(Database database, IReadOnlyList<ObjectId> sectionPolylineIds, double buffer)
        {
            var extents = new List<Extents2d>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in sectionPolylineIds)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null)
                    {
                        continue;
                    }

                    var polylineExtents = polyline.GeometricExtents;
                    var bufferExtents = new Extents2d(
                        new Point2d(polylineExtents.MinPoint.X - buffer, polylineExtents.MinPoint.Y - buffer),
                        new Point2d(polylineExtents.MaxPoint.X + buffer, polylineExtents.MaxPoint.Y + buffer));
                    extents.Add(bufferExtents);
                }

                transaction.Commit();
            }

            return extents;
        }

        private static HashSet<string> BuildExistingFeatureKeys(Database database, Logger logger)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null)
                    {
                        continue;
                    }

                    var key = BuildFeatureKey(polyline, id, logger);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        keys.Add(key);
                    }
                }

                transaction.Commit();
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
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in newIds)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForWrite) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        summary.FilteredDispositions++;
                        polyline?.Erase();
                        continue;
                    }

                    var extents = polyline.GeometricExtents;
                    var extents2d = new Extents2d(
                        new Point2d(extents.MinPoint.X, extents.MinPoint.Y),
                        new Point2d(extents.MaxPoint.X, extents.MaxPoint.Y));
                    if (!IsWithinSections(extents2d, sectionExtents))
                    {
                        summary.FilteredDispositions++;
                        polyline.Erase();
                        continue;
                    }

                    var key = BuildFeatureKey(polyline, id, logger);
                    if (!string.IsNullOrWhiteSpace(key) && existingKeys.Contains(key))
                    {
                        summary.DedupedDispositions++;
                        polyline.Erase();
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        existingKeys.Add(key);
                    }

                    dispositionPolylines.Add(id);
                }

                transaction.Commit();
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
                {
                    return true;
                }
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

        private static bool TryImportShapefile(Type mapImportType, string shapefilePath, Logger logger)
        {
            try
            {
                var mapImport = Activator.CreateInstance(mapImportType);
                SetProperty(mapImportType, mapImport, "SourceFile", shapefilePath);
                SetProperty(mapImportType, mapImport, "FileName", shapefilePath);
                SetProperty(mapImportType, mapImport, "CreateObjectData", true);
                SetProperty(mapImportType, mapImport, "UseObjectData", true);
                SetProperty(mapImportType, mapImport, "ImportPolylines", true);
                SetProperty(mapImportType, mapImport, "ImportClosedPolylines", true);

                var initMethod = mapImportType.GetMethod("Init");
                initMethod?.Invoke(mapImport, new object[] { "MAPIMPORT" });

                if (InvokeIfExists(mapImportType, mapImport, "Import", logger))
                {
                    logger.WriteLine("MapImport.Import invoked.");
                    return true;
                }

                if (InvokeIfExists(mapImportType, mapImport, "Run", logger))
                {
                    logger.WriteLine("MapImport.Run invoked.");
                    return true;
                }

                if (InvokeIfExists(mapImportType, mapImport, "Execute", logger))
                {
                    logger.WriteLine("MapImport.Execute invoked.");
                    return true;
                }

                logger.WriteLine("MapImport executed without a valid import method.");
                return false;
            }
            catch (Exception ex)
            {
                logger.WriteLine("Shapefile import failed: " + ex.Message);
                return false;
            }
        }

        private static void SetProperty(Type type, object instance, string propertyName, object value)
        {
            var property = type.GetProperty(propertyName);
            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value);
            }
        }

        private static bool InvokeIfExists(Type type, object instance, string methodName, Logger logger)
        {
            var method = type.GetMethod(methodName, Type.EmptyTypes);
            if (method != null)
            {
                logger.WriteLine($"Invoking MapImport.{methodName}.");
                method.Invoke(instance, null);
                return true;
            }

            return false;
        }

        private static void LogShapefileSidecars(string shapefilePath, Logger logger)
        {
            var basePath = Path.Combine(
                Path.GetDirectoryName(shapefilePath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(shapefilePath));
            var required = new[] { ".shp", ".shx", ".dbf" };
            foreach (var extension in required)
            {
                var candidate = basePath + extension;
                if (!File.Exists(candidate))
                {
                    logger.WriteLine($"Missing shapefile sidecar: {candidate}");
                }
            }
        }
    }
}
