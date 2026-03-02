/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using AtsBackgroundBuilder.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
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
                var quarterVisibility = QuarterVisibilityPolicy.Create(
                    input.IncludeAtsFabric,
                    input.AllowMultiQuarterDispositions,
                    EnableQuarterViewByEnvironment);

                // L-QUATER display should follow 1/4 Definitions toggle (or env override),
                // regardless of ATS fabric.
                if (!quarterVisibility.ShowQuarterDefinitionView)
                {
                    EraseEntitiesOnLayerWithinSectionWindows(
                        database,
                        sectionDrawResult.SectionPolylineIds,
                        LayerQuarterView,
                        logger,
                        "1/4 definition quarter view");
                }

                // Keep internal quarter helper lines when ATS fabric is requested; otherwise
                // remove visible helper lines if 1/4 definition display is off.
                if (!quarterVisibility.KeepQuarterHelperLinework)
                {
                    EraseEntitiesByLayer(
                        database,
                        sectionDrawResult.QuarterHelperEntityIds,
                        "L-QSEC",
                        logger,
                        "1/4 definition lines");
                }

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

                // Helper construction geometry should never ship in the final drawing.
                EraseEntitiesOnLayer(database, "L-QSEC-BOX", logger, "L-QSEC-BOX helper");
                TryDeleteLayer(database, "L-QSEC-BOX", logger);
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

        private static void EraseEntitiesByLayer(
            Database database,
            IEnumerable<ObjectId> ids,
            string layerName,
            Logger logger,
            string label)
        {
            if (database == null || ids == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

            var unique = ids.Where(id => !id.IsNull).Distinct().ToList();
            if (unique.Count == 0)
            {
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var erased = 0;
                foreach (var id in unique)
                {
                    try
                    {
                        var entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (entity == null || entity.IsErased)
                        {
                            continue;
                        }

                        if (!string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        entity.Erase(true);
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

        private static void EraseEntitiesOnLayerWithinSectionWindows(
            Database database,
            IEnumerable<ObjectId> sectionIds,
            string layerName,
            Logger logger,
            string label)
        {
            if (database == null || sectionIds == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

            var uniqueSectionIds = sectionIds.Where(id => !id.IsNull).Distinct().ToList();
            if (uniqueSectionIds.Count == 0)
            {
                return;
            }

            const double cleanupWindowPadding = 80.0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var windows = new List<Extents3d>();
                foreach (var sectionId in uniqueSectionIds)
                {
                    try
                    {
                        var section = tr.GetObject(sectionId, OpenMode.ForRead, false) as Entity;
                        if (section == null || section.IsErased)
                        {
                            continue;
                        }

                        var extents = section.GeometricExtents;
                        windows.Add(new Extents3d(
                            new Point3d(extents.MinPoint.X - cleanupWindowPadding, extents.MinPoint.Y - cleanupWindowPadding, extents.MinPoint.Z),
                            new Point3d(extents.MaxPoint.X + cleanupWindowPadding, extents.MaxPoint.Y + cleanupWindowPadding, extents.MaxPoint.Z)));
                    }
                    catch
                    {
                        // best effort only
                    }
                }

                if (windows.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var erased = 0;

                foreach (ObjectId id in modelSpace)
                {
                    Entity? entity;
                    try
                    {
                        entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }

                    if (entity == null || entity.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase))
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

                    var inScope = false;
                    for (var i = 0; i < windows.Count; i++)
                    {
                        if (DoExtentsOverlapOrTouchForCleanup(entityExtents, windows[i], 0.01))
                        {
                            inScope = true;
                            break;
                        }
                    }

                    if (!inScope)
                    {
                        continue;
                    }

                    try
                    {
                        entity.Erase(true);
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
                    logger?.WriteLine($"Cleanup: erased {erased} {label} entities in requested section scope");
                }
            }
        }

        private static bool DoExtentsOverlapOrTouchForCleanup(Extents3d a, Extents3d b, double tolerance)
        {
            return a.MinPoint.X <= (b.MaxPoint.X + tolerance) &&
                   a.MaxPoint.X >= (b.MinPoint.X - tolerance) &&
                   a.MinPoint.Y <= (b.MaxPoint.Y + tolerance) &&
                   a.MaxPoint.Y >= (b.MinPoint.Y - tolerance);
        }

        private static void EraseEntitiesOnLayer(Database database, string layerName, Logger logger, string label)
        {
            if (database == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var erased = 0;

                foreach (ObjectId id in modelSpace)
                {
                    Entity? entity;
                    try
                    {
                        entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }

                    if (entity == null || entity.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(entity.Layer, layerName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        entity.Erase(true);
                        erased++;
                    }
                    catch
                    {
                    }
                }

                tr.Commit();
                if (erased > 0)
                {
                    logger?.WriteLine($"Cleanup: erased {erased} {label} entities by layer");
                }
            }
        }

        private static void TryDeleteLayer(Database database, string layerName, Logger logger)
        {
            if (database == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var layerTable = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
                    if (!layerTable.Has(layerName))
                    {
                        tr.Commit();
                        return;
                    }

                    var layerId = layerTable[layerName];
                    if (database.Clayer == layerId && layerTable.Has("0"))
                    {
                        database.Clayer = layerTable["0"];
                    }

                    var purgeCandidates = new ObjectIdCollection();
                    purgeCandidates.Add(layerId);
                    database.Purge(purgeCandidates);
                    if (purgeCandidates.Count == 0)
                    {
                        tr.Commit();
                        logger?.WriteLine($"Cleanup: helper layer {layerName} retained (still referenced).");
                        return;
                    }

                    var layer = tr.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                    if (layer == null || layer.IsErased || layer.IsDependent)
                    {
                        tr.Commit();
                        return;
                    }

                    if (layer.IsLocked)
                    {
                        layer.IsLocked = false;
                    }

                    layer.Erase(true);
                    tr.Commit();
                    logger?.WriteLine($"Cleanup: deleted helper layer {layerName}.");
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine($"Cleanup: unable to delete layer {layerName}: {ex.Message}");
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
                string.Equals(normalized, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase);
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
    }
}

