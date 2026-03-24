using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class NaturalRegionLookupService
    {
        private const string NaturalRegionShapefilePath = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\REGIONS\Natural_Subregions_of_Alberta__2005_.shp";
        private const double BoundaryToleranceMeters = 0.25;
        private const double ImportWindowBufferMeters = 100.0;
        private const int MaxObjectDataTableNameLength = 25;
        private const int MaxObjectDataFieldNameLength = 31;

        private static readonly string[] MapAssemblyNames =
        {
            "ManagedMapApi",
            "Autodesk.Map.Platform",
            "AcMapMgd",
            "AcMapPlatform"
        };

        private static readonly string[] ImportDataMappingCandidates =
        {
            "NewObjectDataOnly",
            "NewObjectData",
            "ObjectDataOnly"
        };

        private static readonly int[] ImportDataMappingFallbackValues = { 1 };

        public bool TryCollectSubRegionText(
            Document doc,
            Editor editor,
            IntPtr hostWindowHandle,
            out string subRegionText,
            out bool keptLinework,
            out string message)
        {
            subRegionText = string.Empty;
            keptLinework = false;
            message = string.Empty;

            if (doc == null)
            {
                message = "Document is required.";
                return false;
            }

            if (editor == null)
            {
                message = "Editor is required.";
                return false;
            }

            if (!File.Exists(NaturalRegionShapefilePath))
            {
                message = $"Natural region shapefile was not found: {NaturalRegionShapefilePath}";
                return false;
            }

            using (doc.LockDocument())
            {
                if (!TryCollectClosedBoundarySelections(
                        editor,
                        doc.Database,
                        hostWindowHandle,
                        out var boundaries,
                        out message))
                {
                    return false;
                }

                if (!TryImportNaturalRegionEntities(
                        doc.Database,
                        boundaries,
                        out var activeProject,
                        out var odTableName,
                        out var importedEntityIds,
                        out message))
                {
                    return false;
                }

                if (importedEntityIds.Count == 0)
                {
                    message = "No natural region entities were imported for the selected footprint area.";
                    return false;
                }

                try
                {
                    EvaluationSummary evaluation;
                    using (var readTransaction = doc.Database.TransactionManager.StartTransaction())
                    {
                        evaluation = EvaluateImportedEntities(readTransaction, importedEntityIds, boundaries, activeProject, odTableName);
                        readTransaction.Commit();
                    }

                    if (evaluation.UniqueTexts.Count == 0)
                    {
                        TryEraseEntities(doc.Database, importedEntityIds);

                        message = evaluation.OverlappingEntityCount > 0
                            ? "Natural region linework overlapped the selected footprint, but NSRNAME/NRNAME object data could not be read from the imported entities. Imported linework was cleaned up."
                            : "No natural sub-region matched the selected footprint. Imported linework was cleaned up.";
                        return false;
                    }

                    if (evaluation.UniqueTexts.Count == 1)
                    {
                        subRegionText = evaluation.UniqueTexts[0];
                        TryEraseEntities(doc.Database, importedEntityIds);

                        message = "Collected natural sub-region text and removed the temporary imported linework.";
                        return true;
                    }

                    subRegionText = JoinReadableList(evaluation.UniqueTexts);
                    keptLinework = true;

                    var entitiesToErase = importedEntityIds
                        .Where(id => !evaluation.MatchedEntityIds.Contains(id))
                        .ToList();
                    TryEraseEntities(doc.Database, entitiesToErase);

                    message = "Multiple natural sub-regions matched the selected footprint. Matching imported linework was kept in the drawing for review.";
                    return true;
                }
                catch (System.Exception ex)
                {
                    TryEraseEntities(doc.Database, importedEntityIds);
                    message = "Natural region lookup failed: " + ex.Message;
                    return false;
                }
            }
        }

        private static bool TryCollectClosedBoundarySelections(
            Editor editor,
            Database database,
            IntPtr hostWindowHandle,
            out List<BoundarySelection> boundaries,
            out string message)
        {
            _ = hostWindowHandle;

            boundaries = new List<BoundarySelection>();
            message = string.Empty;

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect one or more closed proposed-footprint boundaries: "
            };

            RefreshEditorPrompt(editor);
            var selectionResult = editor.GetSelection(options);
            if (selectionResult.Status != PromptStatus.OK)
            {
                message = "Boundary selection was canceled.";
                return false;
            }

            using var transaction = database.TransactionManager.StartTransaction();
            foreach (SelectedObject selected in selectionResult.Value)
            {
                if (selected?.ObjectId.IsNull != false)
                {
                    continue;
                }

                if (transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) is not Entity entity || entity.IsErased)
                {
                    continue;
                }

                if (!TryGetClosedBoundaryVertices(transaction, entity, out var vertices))
                {
                    continue;
                }

                if (!TryGetEntityExtents(entity, vertices, out var extents))
                {
                    continue;
                }

                boundaries.Add(new BoundarySelection(selected.ObjectId, vertices, extents));
            }

            transaction.Commit();

            if (boundaries.Count == 0)
            {
                message = "No closed proposed-footprint boundaries were selected.";
                return false;
            }

            return true;
        }

        private static EvaluationSummary EvaluateImportedEntities(
            Transaction transaction,
            IReadOnlyList<ObjectId> importedEntityIds,
            IReadOnlyList<BoundarySelection> boundaries,
            object? activeProject,
            string odTableName)
        {
            var uniqueTexts = new List<string>();
            var matchedEntityIds = new HashSet<ObjectId>();
            var overlappingEntityCount = 0;

            foreach (var entityId in importedEntityIds.Distinct())
            {
                if (entityId.IsNull)
                {
                    continue;
                }

                if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity || entity.IsErased)
                {
                    continue;
                }

                if (!DoesEntityOverlapSelectedBoundaries(entity, transaction, boundaries))
                {
                    continue;
                }

                overlappingEntityCount++;

                if (!TryReadNaturalRegionText(activeProject, odTableName, entityId, out var text) ||
                    string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                matchedEntityIds.Add(entityId);
                if (!uniqueTexts.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    uniqueTexts.Add(text);
                }
            }

            uniqueTexts.Sort(StringComparer.OrdinalIgnoreCase);
            return new EvaluationSummary(uniqueTexts, matchedEntityIds, overlappingEntityCount);
        }

        private static bool DoesEntityOverlapSelectedBoundaries(
            Entity entity,
            Transaction transaction,
            IReadOnlyList<BoundarySelection> boundaries)
        {
            if (entity == null || boundaries == null || boundaries.Count == 0)
            {
                return false;
            }

            if (!TryGetEntityExtents(entity, null, out var entityExtents))
            {
                return false;
            }

            var candidateBoundaries = boundaries
                .Where(boundary => ExtentsOverlap(boundary.Extents, entityExtents))
                .ToList();
            if (candidateBoundaries.Count == 0)
            {
                return false;
            }

            if (TryGetEntityPolygonVertices(entity, transaction, out var entityVertices))
            {
                foreach (var boundary in candidateBoundaries)
                {
                    if (PolygonsOverlap(boundary.Vertices, entityVertices))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (TryGetRepresentativePoint(entity, transaction, out var point))
            {
                foreach (var boundary in candidateBoundaries)
                {
                    if (IsPointInsidePolygon(boundary.Vertices, new Point2d(point.X, point.Y), BoundaryToleranceMeters))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadNaturalRegionText(object? activeProject, string odTableName, ObjectId entityId, out string text)
        {
            text = string.Empty;
            if (activeProject == null)
            {
                return false;
            }

            var odTables = GetMemberValue(activeProject, "ODTables")
                ?? GetMemberValue(activeProject, "ObjectDataTables")
                ?? GetMemberValue(activeProject, "Tables");
            if (odTables == null)
            {
                return false;
            }

            if (!TryGetOdTableByName(odTables, odTableName, out var table) || table == null)
            {
                return false;
            }

            try
            {
                if (!TryReadNaturalRegionNamesFromTable(table, entityId, out var subRegionName, out var regionName))
                {
                    return false;
                }

                text = FormatNaturalRegionText(subRegionName, regionName);
                return !string.IsNullOrWhiteSpace(text);
            }
            finally
            {
                TryDispose(table);
            }
        }

        private static bool TryReadNaturalRegionNamesFromTable(
            object table,
            ObjectId entityId,
            out string subRegionName,
            out string regionName)
        {
            subRegionName = string.Empty;
            regionName = string.Empty;

            var definitions = GetMemberValue(table, "FieldDefinitions")
                ?? GetMemberValue(table, "Fields")
                ?? GetMemberValue(table, "Columns");
            if (!TryBuildFieldIndexMap(definitions, out var fieldIndexByName) ||
                !fieldIndexByName.TryGetValue("NSRNAME", out var subRegionIndex) ||
                !fieldIndexByName.TryGetValue("NRNAME", out var regionIndex))
            {
                return false;
            }

            if (!TryGetObjectTableRecords(table, entityId, out var records) || records == null)
            {
                return false;
            }

            try
            {
                foreach (var record in EnumerateObjects(records))
                {
                    var subRegionValue = TryReadRecordFieldValue(record, subRegionIndex);
                    var regionValue = TryReadRecordFieldValue(record, regionIndex);
                    if (string.IsNullOrWhiteSpace(subRegionValue) || string.IsNullOrWhiteSpace(regionValue))
                    {
                        continue;
                    }

                    subRegionName = subRegionValue.Trim();
                    regionName = regionValue.Trim();
                    return true;
                }
            }
            finally
            {
                TryDispose(records);
            }

            return false;
        }

        private static bool TryBuildFieldIndexMap(object? definitions, out Dictionary<string, int> fieldIndexByName)
        {
            fieldIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (definitions == null)
            {
                return false;
            }

            var index = 0;
            foreach (var definition in EnumerateObjects(definitions))
            {
                var fieldName = Convert.ToString(
                    GetMemberValue(definition, "Name")
                    ?? GetMemberValue(definition, "FieldName")
                    ?? GetMemberValue(definition, "ColumnName"),
                    CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(fieldName) && !fieldIndexByName.ContainsKey(fieldName))
                {
                    fieldIndexByName[fieldName] = index;
                }

                index++;
            }

            return fieldIndexByName.Count > 0;
        }

        private static bool TryGetObjectTableRecords(object table, ObjectId entityId, out object? records)
        {
            records = null;
            return TryInvokeObjectRecordFetcher(table, "GetObjectTableRecords", entityId, out records)
                   || TryInvokeObjectRecordFetcher(table, "GetObjectRecords", entityId, out records);
        }

        private static bool TryInvokeObjectRecordFetcher(
            object target,
            string methodName,
            ObjectId entityId,
            out object? result)
        {
            result = null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var methods = target.GetType().GetMethods(flags)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(method => method.GetParameters().Length);

            foreach (var method in methods)
            {
                if (!TryBuildObjectRecordFetcherArguments(method, entityId, out var arguments))
                {
                    continue;
                }

                try
                {
                    result = method.Invoke(target, arguments);
                    return result != null;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryBuildObjectRecordFetcherArguments(
            MethodInfo method,
            ObjectId entityId,
            out object?[] arguments)
        {
            arguments = Array.Empty<object?>();
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return false;
            }

            var built = new object?[parameters.Length];
            var assignedObjectId = false;

            for (var index = 0; index < parameters.Length; index++)
            {
                var parameterType = parameters[index].ParameterType.IsByRef
                    ? parameters[index].ParameterType.GetElementType() ?? parameters[index].ParameterType
                    : parameters[index].ParameterType;

                if (!assignedObjectId && parameterType == typeof(ObjectId))
                {
                    built[index] = entityId;
                    assignedObjectId = true;
                    continue;
                }

                if (parameterType.IsEnum)
                {
                    built[index] = GetEnumValue(parameterType, 0, "OpenForRead", "OpenRead", "Read", "kForRead");
                    continue;
                }

                if (parameterType == typeof(bool))
                {
                    built[index] = false;
                    continue;
                }

                if (IsIntegerType(parameterType))
                {
                    built[index] = Convert.ChangeType(0, parameterType, CultureInfo.InvariantCulture);
                    continue;
                }

                if (!parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null)
                {
                    built[index] = null;
                    continue;
                }

                return false;
            }

            if (!assignedObjectId)
            {
                return false;
            }

            arguments = built;
            return true;
        }

        private static string TryReadRecordFieldValue(object record, int index)
        {
            if ((!TryInvoke(record, "get_Item", out var mapValue, index) && !TryInvoke(record, "Item", out mapValue, index))
                || mapValue == null)
            {
                return string.Empty;
            }

            foreach (var candidate in new[] { "StrValue", "StringValue", "Value", "Text", "DisplayValue", "Obj" })
            {
                var value = GetMemberValue(mapValue, candidate);
                var asText = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
                if (!string.IsNullOrWhiteSpace(asText) &&
                    !LooksLikeTypeName(asText))
                {
                    return asText;
                }
            }

            try
            {
                var fallback = Convert.ToString(mapValue, CultureInfo.InvariantCulture)?.Trim();
                return LooksLikeTypeName(fallback) ? string.Empty : fallback ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FormatNaturalRegionText(string subRegionName, string regionName)
        {
            var safeSubRegionName = (subRegionName ?? string.Empty).Trim();
            var safeRegionName = (regionName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(safeSubRegionName) || string.IsNullOrWhiteSpace(safeRegionName))
            {
                return string.Empty;
            }

            if (safeRegionName.EndsWith("Natural Region", StringComparison.OrdinalIgnoreCase))
            {
                return $"{safeSubRegionName} Sub-region of the {safeRegionName}";
            }

            if (safeRegionName.EndsWith("Region", StringComparison.OrdinalIgnoreCase))
            {
                safeRegionName = safeRegionName[..^"Region".Length].TrimEnd() + " Natural Region";
            }
            else
            {
                safeRegionName += " Natural Region";
            }

            return $"{safeSubRegionName} Sub-region of the {safeRegionName}";
        }

        private static bool TryImportNaturalRegionEntities(
            Database database,
            IReadOnlyList<BoundarySelection> boundaries,
            out object? activeProject,
            out string odTableName,
            out List<ObjectId> importedEntityIds,
            out string message)
        {
            activeProject = null;
            odTableName = string.Empty;
            importedEntityIds = new List<ObjectId>();
            message = string.Empty;

            var mapApplication = TryGetMapApplication();
            if (mapApplication == null)
            {
                message = "Map import API is unavailable. Run this in AutoCAD Map 3D/Civil 3D.";
                return false;
            }

            activeProject = GetMemberValue(mapApplication, "ActiveProject");
            if (activeProject == null)
            {
                message = "No active Map project was found.";
                return false;
            }

            var odTables = GetMemberValue(activeProject, "ODTables")
                ?? GetMemberValue(activeProject, "ObjectDataTables")
                ?? GetMemberValue(activeProject, "Tables");
            if (odTables == null)
            {
                message = "Map Object Data tables are unavailable in the current drawing.";
                return false;
            }

            var importer = GetMemberValue(mapApplication, "Importer");
            if (importer == null)
            {
                message = "Map importer service is unavailable.";
                return false;
            }

            if (!TryInvoke(importer, "Init", out _, "SHP", NaturalRegionShapefilePath))
            {
                message = $"Could not initialize the Map importer for {NaturalRegionShapefilePath}.";
                return false;
            }

            if (!TrySetImporterLocationWindow(importer, boundaries))
            {
                message = "The natural-region importer could not apply a location window, so the full dataset was not imported.";
                return false;
            }

            odTableName = ResolveAvailableOdTableName(odTables);
            if (string.IsNullOrWhiteSpace(odTableName))
            {
                message = "Could not reserve an Object Data table name for the natural-region import.";
                return false;
            }

            if (!ApplyObjectDataMapping(importer, odTableName))
            {
                message = "Could not map the natural-region shapefile fields into Object Data.";
                return false;
            }

            foreach (var layer in EnumerateInputLayers(importer))
            {
                TrySetMemberValue(layer, "ImportFromInputLayerOn", true);
            }

            var preImportHandles = CaptureModelSpaceHandles(database);
            var previousMapUseMPolygon = TryGetSystemVariable("MAPUSEMPOLYGON", out var mapUseMPolygonValue)
                ? mapUseMPolygonValue
                : null;
            var previousPolyDisplay = TryGetSystemVariable("POLYDISPLAY", out var polyDisplayValue)
                ? polyDisplayValue
                : null;

            TrySetSystemVariable("MAPUSEMPOLYGON", 0);
            TrySetSystemVariable("POLYDISPLAY", 1);

            try
            {
                if (!TryInvoke(importer, "Import", out _, false) &&
                    !TryInvoke(importer, "Import", out _, true) &&
                    !TryInvoke(importer, "Import", out _))
                {
                    message = "Natural region shapefile import failed.";
                    return false;
                }
            }
            finally
            {
                RestoreSystemVariable("MAPUSEMPOLYGON", previousMapUseMPolygon);
                RestoreSystemVariable("POLYDISPLAY", previousPolyDisplay);
            }

            importedEntityIds = CaptureNewModelSpaceEntityIds(database, preImportHandles);
            if (importedEntityIds.Count == 0)
            {
                message = "Natural region shapefile import completed, but no entities were created in model space.";
                return false;
            }

            return true;
        }

        private static HashSet<long> CaptureModelSpaceHandles(Database database)
        {
            var handles = new HashSet<long>();
            try
            {
                using var transaction = database.TransactionManager.StartTransaction();
                var modelSpace = GetModelSpaceRecord(database, transaction, OpenMode.ForRead);
                foreach (ObjectId entityId in modelSpace)
                {
                    handles.Add(entityId.Handle.Value);
                }
            }
            catch
            {
            }

            return handles;
        }

        private static List<ObjectId> CaptureNewModelSpaceEntityIds(Database database, ISet<long> preImportHandles)
        {
            var imported = new List<ObjectId>();
            try
            {
                using var transaction = database.TransactionManager.StartTransaction();
                var modelSpace = GetModelSpaceRecord(database, transaction, OpenMode.ForRead);
                foreach (ObjectId entityId in modelSpace)
                {
                    if (preImportHandles.Contains(entityId.Handle.Value))
                    {
                        continue;
                    }

                    imported.Add(entityId);
                }
            }
            catch
            {
            }

            return imported;
        }

        private static BlockTableRecord GetModelSpaceRecord(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private static bool TrySetImporterLocationWindow(object importer, IReadOnlyList<BoundarySelection> boundaries)
        {
            if (importer == null || boundaries == null || boundaries.Count == 0)
            {
                return false;
            }

            var union = UnionExtents(boundaries.Select(boundary => boundary.Extents));
            var minX = union.MinPoint.X - ImportWindowBufferMeters;
            var minY = union.MinPoint.Y - ImportWindowBufferMeters;
            var maxX = union.MaxPoint.X + ImportWindowBufferMeters;
            var maxY = union.MaxPoint.Y + ImportWindowBufferMeters;

            try
            {
                var method = importer.GetType().GetMethod("SetLocationWindowAndOptions", BindingFlags.Public | BindingFlags.Instance);
                if (method == null)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 5 ||
                    parameters[0].ParameterType != typeof(double) ||
                    parameters[1].ParameterType != typeof(double) ||
                    parameters[2].ParameterType != typeof(double) ||
                    parameters[3].ParameterType != typeof(double) ||
                    !parameters[4].ParameterType.IsEnum)
                {
                    return false;
                }

                var option = GetEnumValue(parameters[4].ParameterType, 2, "kUseLocationWindow", "UseLocationWindow");
                try
                {
                    method.Invoke(importer, new object[] { minX, minY, maxX, maxY, option });
                    return true;
                }
                catch
                {
                    try
                    {
                        method.Invoke(importer, new object[] { minX, maxX, minY, maxY, option });
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool ApplyObjectDataMapping(object importer, string tableName)
        {
            var mappedLayerCount = 0;

            foreach (var inputLayer in EnumerateInputLayers(importer))
            {
                TrySetMemberValue(inputLayer, "ImportFromInputLayerOn", true);

                if (TryConfigureObjectDataMapping(inputLayer, tableName))
                {
                    mappedLayerCount++;
                }

                var columnIndex = 1;
                foreach (var column in EnumerateLayerColumns(inputLayer))
                {
                    var columnName = Convert.ToString(
                        GetMemberValue(column, "ColumnName")
                        ?? GetMemberValue(column, "Name")
                        ?? GetMemberValue(column, "FieldName"),
                        CultureInfo.InvariantCulture);

                    var fieldName = SanitizeFieldName(columnName, columnIndex);
                    TryConfigureColumnMapping(column, fieldName);
                    columnIndex++;
                }
            }

            return mappedLayerCount > 0;
        }

        private static IEnumerable<object> EnumerateInputLayers(object importer)
        {
            var yielded = new List<object>();

            foreach (var layer in EnumerateObjects(GetMemberValue(importer, "InputLayers")))
            {
                if (TryRememberUnique(yielded, layer))
                {
                    yield return layer;
                }
            }

            foreach (var layer in EnumerateObjects(GetMemberValue(importer, "Layers")))
            {
                if (TryRememberUnique(yielded, layer))
                {
                    yield return layer;
                }
            }

            foreach (var layer in EnumerateObjects(GetMemberValue(importer, "InputLayerIterator")))
            {
                if (TryRememberUnique(yielded, layer))
                {
                    yield return layer;
                }
            }

            foreach (var layer in EnumerateObjects(importer))
            {
                if (TryRememberUnique(yielded, layer))
                {
                    yield return layer;
                }
            }
        }

        private static IEnumerable<object> EnumerateLayerColumns(object inputLayer)
        {
            var yielded = new List<object>();

            foreach (var source in new[]
                     {
                         GetMemberValue(inputLayer, "Columns"),
                         GetMemberValue(inputLayer, "ColumnIterator"),
                         GetMemberValue(inputLayer, "ColumnMappings"),
                         GetMemberValue(inputLayer, "Fields"),
                         GetMemberValue(inputLayer, "Attributes")
                     })
            {
                foreach (var column in EnumerateObjects(source))
                {
                    if (TryRememberUnique(yielded, column))
                    {
                        yield return column;
                    }
                }
            }
        }

        private static bool TryConfigureObjectDataMapping(object inputLayer, string tableName)
        {
            foreach (var mappingValue in ResolveImportDataMappingValues(inputLayer))
            {
                if (TryInvoke(inputLayer, "SetDataMapping", out _, mappingValue, tableName)
                    || TryInvoke(inputLayer, "SetDataMapping", out _, tableName, mappingValue))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<object> ResolveImportDataMappingValues(object inputLayer)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var results = new List<object>();
            var methods = inputLayer.GetType().GetMethods(flags)
                .Where(method => string.Equals(method.Name, "SetDataMapping", StringComparison.OrdinalIgnoreCase));

            foreach (var method in methods)
            {
                foreach (var parameter in method.GetParameters())
                {
                    var parameterType = parameter.ParameterType.IsByRef
                        ? parameter.ParameterType.GetElementType() ?? parameter.ParameterType
                        : parameter.ParameterType;

                    if (!parameterType.IsEnum)
                    {
                        continue;
                    }

                    foreach (var candidate in ImportDataMappingCandidates)
                    {
                        try
                        {
                            var enumValue = Enum.Parse(parameterType, candidate, ignoreCase: true);
                            TryRememberUnique(results, enumValue);
                        }
                        catch
                        {
                        }
                    }

                    foreach (var fallbackValue in ImportDataMappingFallbackValues)
                    {
                        try
                        {
                            var enumValue = Enum.ToObject(parameterType, fallbackValue);
                            TryRememberUnique(results, enumValue);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            foreach (var fallbackValue in ImportDataMappingFallbackValues)
            {
                TryRememberUnique(results, fallbackValue);
            }

            return results;
        }

        private static bool TryConfigureColumnMapping(object column, string fieldName)
        {
            return TryInvoke(column, "SetColumnDataMapping", out _, fieldName)
                   || TryInvoke(column, "SetColumnDataMapping", out _, true, fieldName)
                   || TryInvoke(column, "SetDataMapping", out _, fieldName)
                   || TryInvoke(column, "SetDataMapping", out _, true, fieldName)
                   || TrySetAnyMemberValue(
                       column,
                       fieldName,
                       "ColumnDataMapping",
                       "TargetColumn",
                       "TargetField",
                       "FieldName");
        }

        private static bool TrySetAnyMemberValue(object target, object? value, params string[] memberNames)
        {
            foreach (var memberName in memberNames)
            {
                if (TrySetMemberValue(target, memberName, value))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ResolveAvailableOdTableName(object odTables)
        {
            for (var index = 1; index < 1000; index++)
            {
                var candidate = BuildSafeName("WLSNATREG", "WLSNATREG", index, MaxObjectDataTableNameLength);
                if (!TryInvoke(odTables, "IsTableDefined", out var definedResult, candidate) || !ToBool(definedResult))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static bool TryGetOdTableByName(object odTables, string tableName, out object? table)
        {
            table = null;
            return (TryInvoke(odTables, "get_Item", out table, tableName) || TryInvoke(odTables, "Item", out table, tableName))
                   && table != null;
        }

        private static bool TryDeleteObjectDataTable(object? activeProject, string tableName)
        {
            if (activeProject == null || string.IsNullOrWhiteSpace(tableName))
            {
                return false;
            }

            var odTables = GetMemberValue(activeProject, "ODTables")
                ?? GetMemberValue(activeProject, "ObjectDataTables")
                ?? GetMemberValue(activeProject, "Tables");
            if (odTables == null)
            {
                return false;
            }

            foreach (var methodName in new[] { "Remove", "Delete", "RemoveTable" })
            {
                if (TryInvoke(odTables, methodName, out _, tableName))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildSafeName(string? rawName, string fallbackPrefix, int index, int maxLength)
        {
            var cleaned = new System.Text.StringBuilder();
            var source = string.IsNullOrWhiteSpace(rawName) ? string.Empty : rawName.Trim();

            foreach (var c in source)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    cleaned.Append(c);
                }
                else if (cleaned.Length == 0 || cleaned[cleaned.Length - 1] != '_')
                {
                    cleaned.Append('_');
                }

                if (cleaned.Length >= Math.Max(1, maxLength - 2))
                {
                    break;
                }
            }

            var candidate = cleaned.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = fallbackPrefix + index.ToString(CultureInfo.InvariantCulture);
            }

            if (!char.IsLetter(candidate[0]))
            {
                candidate = fallbackPrefix + candidate;
            }

            if (candidate.Length > maxLength)
            {
                candidate = candidate.Substring(0, maxLength);
            }

            return candidate;
        }

        private static string SanitizeFieldName(string? rawName, int index)
        {
            var cleaned = new System.Text.StringBuilder();
            foreach (var c in rawName ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    cleaned.Append(c);
                }
                else if (cleaned.Length == 0 || cleaned[cleaned.Length - 1] != '_')
                {
                    cleaned.Append('_');
                }

                if (cleaned.Length >= MaxObjectDataFieldNameLength)
                {
                    break;
                }
            }

            var candidate = cleaned.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = "FIELD" + index.ToString(CultureInfo.InvariantCulture);
            }

            if (!char.IsLetter(candidate[0]))
            {
                candidate = "F_" + candidate;
            }

            if (candidate.Length > MaxObjectDataFieldNameLength)
            {
                candidate = candidate.Substring(0, MaxObjectDataFieldNameLength);
            }

            return candidate;
        }

        private static Extents2d UnionExtents(IEnumerable<Extents2d> extents)
        {
            using var enumerator = extents.GetEnumerator();
            enumerator.MoveNext();
            var union = enumerator.Current;

            while (enumerator.MoveNext())
            {
                var current = enumerator.Current;
                union = new Extents2d(
                    Math.Min(union.MinPoint.X, current.MinPoint.X),
                    Math.Min(union.MinPoint.Y, current.MinPoint.Y),
                    Math.Max(union.MaxPoint.X, current.MaxPoint.X),
                    Math.Max(union.MaxPoint.Y, current.MaxPoint.Y));
            }

            return union;
        }

        private static void EraseEntities(Transaction transaction, IEnumerable<ObjectId> entityIds)
        {
            foreach (var entityId in entityIds.Distinct())
            {
                if (entityId.IsNull)
                {
                    continue;
                }

                try
                {
                    if (transaction.GetObject(entityId, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    {
                        entity.Erase(true);
                    }
                }
                catch
                {
                }
            }
        }

        private static void TryEraseEntities(Database database, IEnumerable<ObjectId> entityIds)
        {
            try
            {
                using var transaction = database.TransactionManager.StartTransaction();
                EraseEntities(transaction, entityIds);
                transaction.Commit();
            }
            catch
            {
            }
        }

        private static bool TryGetClosedBoundaryVertices(
            Transaction transaction,
            Entity entity,
            out List<Point2d> vertices)
        {
            vertices = new List<Point2d>();
            switch (entity)
            {
                case Polyline polyline when polyline.Closed && polyline.NumberOfVertices >= 3:
                    for (var index = 0; index < polyline.NumberOfVertices; index++)
                    {
                        vertices.Add(polyline.GetPoint2dAt(index));
                    }

                    return vertices.Count >= 3;

                case Polyline2d polyline2d when polyline2d.Closed:
                    foreach (ObjectId vertexId in polyline2d)
                    {
                        if (transaction.GetObject(vertexId, OpenMode.ForRead, false) is Vertex2d vertex)
                        {
                            vertices.Add(new Point2d(vertex.Position.X, vertex.Position.Y));
                        }
                    }

                    return vertices.Count >= 3;

                case Polyline3d polyline3d when polyline3d.Closed:
                    foreach (ObjectId vertexId in polyline3d)
                    {
                        if (transaction.GetObject(vertexId, OpenMode.ForRead, false) is PolylineVertex3d vertex)
                        {
                            vertices.Add(new Point2d(vertex.Position.X, vertex.Position.Y));
                        }
                    }

                    return vertices.Count >= 3;

                default:
                    return false;
            }
        }

        private static bool TryGetEntityPolygonVertices(
            Entity entity,
            Transaction transaction,
            out List<Point2d> vertices)
        {
            if (TryGetClosedBoundaryVertices(transaction, entity, out vertices))
            {
                return true;
            }

            if (!LooksPolygonLike(entity))
            {
                vertices = new List<Point2d>();
                return false;
            }

            var exploded = new DBObjectCollection();
            try
            {
                entity.Explode(exploded);
                Polyline? bestPolyline = null;
                var bestArea = -1.0;

                foreach (DBObject dbObject in exploded)
                {
                    if (dbObject is not Polyline polyline || !polyline.Closed || polyline.NumberOfVertices < 3)
                    {
                        continue;
                    }

                    double area;
                    try
                    {
                        area = Math.Abs(polyline.Area);
                    }
                    catch
                    {
                        area = 0.0;
                    }

                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestPolyline = polyline;
                    }
                }

                if (bestPolyline == null)
                {
                    vertices = new List<Point2d>();
                    return false;
                }

                vertices = new List<Point2d>(bestPolyline.NumberOfVertices);
                for (var index = 0; index < bestPolyline.NumberOfVertices; index++)
                {
                    vertices.Add(bestPolyline.GetPoint2dAt(index));
                }

                return vertices.Count >= 3;
            }
            catch
            {
                vertices = new List<Point2d>();
                return false;
            }
            finally
            {
                foreach (DBObject dbObject in exploded)
                {
                    try
                    {
                        dbObject.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool LooksPolygonLike(Entity entity)
        {
            var dxfName = entity.GetRXClass()?.DxfName ?? string.Empty;
            var typeName = entity.GetType().Name ?? string.Empty;

            return string.Equals(dxfName, "POLYGON", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(dxfName, "MPOLYGON", StringComparison.OrdinalIgnoreCase)
                   || typeName.IndexOf("Polygon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetEntityExtents(Entity entity, IReadOnlyList<Point2d>? vertices, out Extents2d extents)
        {
            try
            {
                var geometricExtents = entity.GeometricExtents;
                extents = new Extents2d(
                    geometricExtents.MinPoint.X,
                    geometricExtents.MinPoint.Y,
                    geometricExtents.MaxPoint.X,
                    geometricExtents.MaxPoint.Y);
                return true;
            }
            catch
            {
            }

            if (vertices != null && vertices.Count > 0)
            {
                var minX = vertices.Min(vertex => vertex.X);
                var minY = vertices.Min(vertex => vertex.Y);
                var maxX = vertices.Max(vertex => vertex.X);
                var maxY = vertices.Max(vertex => vertex.Y);
                extents = new Extents2d(minX, minY, maxX, maxY);
                return true;
            }

            extents = default;
            return false;
        }

        private static bool TryGetRepresentativePoint(Entity entity, Transaction transaction, out Point3d point)
        {
            switch (entity)
            {
                case Polyline polyline when polyline.NumberOfVertices > 0:
                    point = polyline.GetPoint3dAt(0);
                    return true;

                case Polyline2d polyline2d:
                    foreach (ObjectId vertexId in polyline2d)
                    {
                        if (transaction.GetObject(vertexId, OpenMode.ForRead, false) is Vertex2d vertex2d)
                        {
                            point = vertex2d.Position;
                            return true;
                        }
                    }

                    break;

                case Polyline3d polyline3d:
                    foreach (ObjectId vertexId in polyline3d)
                    {
                        if (transaction.GetObject(vertexId, OpenMode.ForRead, false) is PolylineVertex3d vertex3d)
                        {
                            point = vertex3d.Position;
                            return true;
                        }
                    }

                    break;

                case Line line:
                    point = new Point3d(
                        (line.StartPoint.X + line.EndPoint.X) * 0.5,
                        (line.StartPoint.Y + line.EndPoint.Y) * 0.5,
                        (line.StartPoint.Z + line.EndPoint.Z) * 0.5);
                    return true;

                case DBPoint dbPoint:
                    point = dbPoint.Position;
                    return true;

                case DBText dbText:
                    point = dbText.Position;
                    return true;

                case MText mText:
                    point = mText.Location;
                    return true;

                case BlockReference blockReference:
                    point = blockReference.Position;
                    return true;
            }

            point = Point3d.Origin;
            return false;
        }

        private static bool PolygonsOverlap(IReadOnlyList<Point2d> firstPolygon, IReadOnlyList<Point2d> secondPolygon)
        {
            if (firstPolygon == null || secondPolygon == null || firstPolygon.Count < 3 || secondPolygon.Count < 3)
            {
                return false;
            }

            foreach (var point in firstPolygon)
            {
                if (IsPointInsidePolygon(secondPolygon, point, BoundaryToleranceMeters))
                {
                    return true;
                }
            }

            foreach (var point in secondPolygon)
            {
                if (IsPointInsidePolygon(firstPolygon, point, BoundaryToleranceMeters))
                {
                    return true;
                }
            }

            for (var firstIndex = 0; firstIndex < firstPolygon.Count; firstIndex++)
            {
                var firstStart = firstPolygon[firstIndex];
                var firstEnd = firstPolygon[(firstIndex + 1) % firstPolygon.Count];

                for (var secondIndex = 0; secondIndex < secondPolygon.Count; secondIndex++)
                {
                    var secondStart = secondPolygon[secondIndex];
                    var secondEnd = secondPolygon[(secondIndex + 1) % secondPolygon.Count];

                    if (DoSegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd, BoundaryToleranceMeters))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsPointInsidePolygon(
            IReadOnlyList<Point2d> vertices,
            Point2d point,
            double tolerance)
        {
            if (vertices == null || vertices.Count < 3)
            {
                return false;
            }

            if (DistanceSqToBoundary(vertices, point) <= tolerance * tolerance)
            {
                return true;
            }

            var inside = false;
            var previous = vertices[vertices.Count - 1];
            for (var index = 0; index < vertices.Count; index++)
            {
                var current = vertices[index];
                if ((previous.Y > point.Y) != (current.Y > point.Y))
                {
                    var intersectX = ((current.X - previous.X) * (point.Y - previous.Y) / (current.Y - previous.Y)) + previous.X;
                    if (point.X < intersectX)
                    {
                        inside = !inside;
                    }
                }

                previous = current;
            }

            return inside;
        }

        private static double DistanceSqToBoundary(IReadOnlyList<Point2d> vertices, Point2d point)
        {
            var minimumDistanceSq = double.MaxValue;
            for (var index = 0; index < vertices.Count; index++)
            {
                var start = vertices[index];
                var end = vertices[(index + 1) % vertices.Count];
                minimumDistanceSq = Math.Min(minimumDistanceSq, DistanceSqToSegment(point, start, end));
            }

            return minimumDistanceSq;
        }

        private static double DistanceSqToSegment(Point2d point, Point2d start, Point2d end)
        {
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var lengthSq = (edgeX * edgeX) + (edgeY * edgeY);
            if (lengthSq <= 0.0)
            {
                var dx = point.X - start.X;
                var dy = point.Y - start.Y;
                return (dx * dx) + (dy * dy);
            }

            var deltaX = point.X - start.X;
            var deltaY = point.Y - start.Y;
            var projection = (deltaX * edgeX) + (deltaY * edgeY);
            var t = Math.Max(0.0, Math.Min(1.0, projection / lengthSq));
            var closestX = start.X + (t * edgeX);
            var closestY = start.Y + (t * edgeY);
            var offsetX = point.X - closestX;
            var offsetY = point.Y - closestY;
            return (offsetX * offsetX) + (offsetY * offsetY);
        }

        private static bool DoSegmentsIntersect(
            Point2d firstStart,
            Point2d firstEnd,
            Point2d secondStart,
            Point2d secondEnd,
            double tolerance)
        {
            if (DistanceSqToSegment(firstStart, secondStart, secondEnd) <= tolerance * tolerance ||
                DistanceSqToSegment(firstEnd, secondStart, secondEnd) <= tolerance * tolerance ||
                DistanceSqToSegment(secondStart, firstStart, firstEnd) <= tolerance * tolerance ||
                DistanceSqToSegment(secondEnd, firstStart, firstEnd) <= tolerance * tolerance)
            {
                return true;
            }

            var firstOrientation = GetOrientation(firstStart, firstEnd, secondStart);
            var secondOrientation = GetOrientation(firstStart, firstEnd, secondEnd);
            var thirdOrientation = GetOrientation(secondStart, secondEnd, firstStart);
            var fourthOrientation = GetOrientation(secondStart, secondEnd, firstEnd);

            return (firstOrientation > 0 && secondOrientation < 0 || firstOrientation < 0 && secondOrientation > 0) &&
                   (thirdOrientation > 0 && fourthOrientation < 0 || thirdOrientation < 0 && fourthOrientation > 0);
        }

        private static double GetOrientation(Point2d start, Point2d end, Point2d point)
        {
            return ((end.X - start.X) * (point.Y - start.Y))
                   - ((end.Y - start.Y) * (point.X - start.X));
        }

        private static bool ExtentsOverlap(Extents2d first, Extents2d second)
        {
            return !(first.MaxPoint.X < second.MinPoint.X ||
                     first.MinPoint.X > second.MaxPoint.X ||
                     first.MaxPoint.Y < second.MinPoint.Y ||
                     first.MinPoint.Y > second.MaxPoint.Y);
        }

        private static void RefreshEditorPrompt(Editor editor)
        {
            if (editor == null)
            {
                return;
            }

            try
            {
                editor.WriteMessage("\n");
                editor.PostCommandPrompt();
            }
            catch
            {
            }
        }

        private static object? TryGetMapApplication()
        {
            foreach (var assemblyName in MapAssemblyNames)
            {
                TryLoadAssembly(assemblyName);
            }

            var hostType = FindType("Autodesk.Gis.Map.HostMapApplicationServices");
            if (hostType != null)
            {
                var app = GetStaticMemberValue(hostType, "Application");
                if (app != null)
                {
                    return app;
                }
            }

            var mapApplicationType = FindType("Autodesk.Gis.Map.MapApplication");
            return mapApplicationType != null
                ? GetStaticMemberValue(mapApplicationType, "Application")
                : null;
        }

        private static void TryLoadAssembly(string assemblyName)
        {
            try
            {
                Assembly.Load(assemblyName);
            }
            catch
            {
            }
        }

        private static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                if (type != null)
                {
                    return type;
                }
            }

            foreach (var assemblyName in MapAssemblyNames)
            {
                try
                {
                    var assembly = Assembly.Load(assemblyName);
                    var type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static object? GetStaticMemberValue(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
            try
            {
                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(null, null);
                }

                var getter = type.GetMethod($"get_{name}", flags, null, Type.EmptyTypes, null);
                if (getter != null)
                {
                    return getter.Invoke(null, Array.Empty<object>());
                }
            }
            catch
            {
            }

            return null;
        }

        private static object? GetMemberValue(object? target, string name)
        {
            if (target == null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            try
            {
                var type = target.GetType();
                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }

                var getter = type.GetMethod($"get_{name}", flags, null, Type.EmptyTypes, null);
                if (getter != null)
                {
                    return getter.Invoke(target, Array.Empty<object>());
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool TrySetMemberValue(object target, string name, object? value)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var type = target.GetType();

            foreach (var property in type.GetProperties(flags).Where(property =>
                         string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                         property.CanWrite))
            {
                if (!TryConvertArgument(value, property.PropertyType, out var converted))
                {
                    continue;
                }

                try
                {
                    property.SetValue(target, converted, null);
                    return true;
                }
                catch
                {
                }
            }

            foreach (var method in type.GetMethods(flags).Where(method =>
                         string.Equals(method.Name, $"set_{name}", StringComparison.OrdinalIgnoreCase)))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                if (!TryConvertArgument(value, parameters[0].ParameterType, out var converted))
                {
                    continue;
                }

                try
                {
                    method.Invoke(target, new[] { converted });
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryInvoke(object target, string methodName, out object? result, params object?[] args)
        {
            result = null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var methods = target.GetType().GetMethods(flags)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(method => method.GetParameters().Length);

            foreach (var method in methods)
            {
                if (!TryPrepareArguments(method, args, out var invokeArguments))
                {
                    continue;
                }

                try
                {
                    result = method.Invoke(target, invokeArguments);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryPrepareArguments(MethodInfo method, object?[] inputArgs, out object?[] invokeArgs)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != inputArgs.Length)
            {
                invokeArgs = Array.Empty<object?>();
                return false;
            }

            invokeArgs = new object?[parameters.Length];
            for (var index = 0; index < parameters.Length; index++)
            {
                if (!TryConvertArgument(inputArgs[index], parameters[index].ParameterType, out var converted))
                {
                    return false;
                }

                invokeArgs[index] = converted;
            }

            return true;
        }

        private static bool TryConvertArgument(object? value, Type targetType, out object? converted)
        {
            var actualTarget = targetType.IsByRef ? targetType.GetElementType() ?? targetType : targetType;

            if (value == null)
            {
                if (!actualTarget.IsValueType || Nullable.GetUnderlyingType(actualTarget) != null)
                {
                    converted = null;
                    return true;
                }

                converted = null;
                return false;
            }

            if (actualTarget.IsInstanceOfType(value))
            {
                converted = value;
                return true;
            }

            if (actualTarget.IsEnum)
            {
                try
                {
                    if (value is string enumText)
                    {
                        converted = Enum.Parse(actualTarget, enumText, ignoreCase: true);
                        return true;
                    }

                    var enumNumeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    converted = Enum.ToObject(actualTarget, enumNumeric);
                    return true;
                }
                catch
                {
                    converted = null;
                    return false;
                }
            }

            try
            {
                converted = Convert.ChangeType(value, actualTarget, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        private static IEnumerable<object> EnumerateObjects(object? source)
        {
            if (source == null)
            {
                yield break;
            }

            if (source is IEnumerable enumerable)
            {
                IEnumerator? enumerator = null;
                try
                {
                    enumerator = enumerable.GetEnumerator();
                }
                catch
                {
                }

                if (enumerator != null)
                {
                    try
                    {
                        while (true)
                        {
                            bool movedNext;
                            try
                            {
                                movedNext = enumerator.MoveNext();
                            }
                            catch
                            {
                                yield break;
                            }

                            if (!movedNext)
                            {
                                yield break;
                            }

                            if (enumerator.Current != null)
                            {
                                yield return enumerator.Current;
                            }
                        }
                    }
                    finally
                    {
                        TryDispose(enumerator);
                    }
                }
            }

            var count = ToInt(GetMemberValue(source, "Count"));
            if (count > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    if (TryGetIndexItem(source, index, out var item) && item != null)
                    {
                        yield return item;
                    }
                }

                yield break;
            }

            var getEnumerator = source.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getEnumerator == null)
            {
                yield break;
            }

            object? customEnumerator = null;
            try
            {
                customEnumerator = getEnumerator.Invoke(source, Array.Empty<object>());
            }
            catch
            {
            }

            while (customEnumerator != null)
            {
                try
                {
                    while (customEnumerator != null)
                    {
                        if (!TryMoveNext(customEnumerator))
                        {
                            yield break;
                        }

                        var current = GetMemberValue(customEnumerator, "Current")
                            ?? GetMemberValue(customEnumerator, "Item")
                            ?? GetMemberValue(customEnumerator, "Value");
                        if (current != null)
                        {
                            yield return current;
                        }
                    }
                }
                finally
                {
                    TryDispose(customEnumerator);
                }
            }
        }

        private static bool TryGetIndexItem(object source, int index, out object? item)
        {
            item = null;

            if (TryInvoke(source, "get_Item", out item, index) && item != null)
            {
                return true;
            }

            var property = source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.GetIndexParameters().Length == 1);
            if (property == null)
            {
                return false;
            }

            try
            {
                item = property.GetValue(source, new object[] { index });
                return item != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryMoveNext(object enumerator)
        {
            return TryInvoke(enumerator, "MoveNext", out _)
                   || TryInvoke(enumerator, "Step", out _)
                   || TryInvoke(enumerator, "Next", out _)
                   || TryInvoke(enumerator, "MoveNext", out _, true);
        }

        private static object GetEnumValue(Type enumType, int fallbackNumericValue, params string[] preferredNames)
        {
            foreach (var preferredName in preferredNames)
            {
                if (string.IsNullOrWhiteSpace(preferredName))
                {
                    continue;
                }

                try
                {
                    return Enum.Parse(enumType, preferredName, ignoreCase: true);
                }
                catch
                {
                }
            }

            return Enum.ToObject(enumType, fallbackNumericValue);
        }

        private static bool IsIntegerType(Type type)
        {
            var actualType = Nullable.GetUnderlyingType(type) ?? type;
            return actualType == typeof(byte)
                   || actualType == typeof(sbyte)
                   || actualType == typeof(short)
                   || actualType == typeof(ushort)
                   || actualType == typeof(int)
                   || actualType == typeof(uint)
                   || actualType == typeof(long)
                   || actualType == typeof(ulong);
        }

        private static string JoinReadableList(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return string.Empty;
            }

            if (values.Count == 1)
            {
                return values[0];
            }

            if (values.Count == 2)
            {
                return $"{values[0]} and {values[1]}";
            }

            return string.Join(", ", values.Take(values.Count - 1)) + $", and {values[^1]}";
        }

        private static bool TryRememberUnique(ICollection<object> target, object? value)
        {
            if (value == null || target.Contains(value))
            {
                return false;
            }

            target.Add(value);
            return true;
        }

        private static int ToInt(object? value)
        {
            if (value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static bool ToBool(object? value)
        {
            if (value == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetSystemVariable(string name, out object? value)
        {
            value = null;
            try
            {
                value = Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable(name);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TrySetSystemVariable(string name, object value)
        {
            try
            {
                Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable(name, value);
            }
            catch
            {
            }
        }

        private static void RestoreSystemVariable(string name, object? value)
        {
            if (value == null)
            {
                return;
            }

            TrySetSystemVariable(name, value);
        }

        private static void TryDispose(object? value)
        {
            if (value is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                }
            }
        }

        private static bool LooksLikeTypeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            return trimmed.StartsWith("Autodesk.", StringComparison.Ordinal)
                   || trimmed.StartsWith("System.", StringComparison.Ordinal);
        }

        private readonly struct BoundarySelection
        {
            public BoundarySelection(ObjectId entityId, IReadOnlyList<Point2d> vertices, Extents2d extents)
            {
                EntityId = entityId;
                Vertices = vertices;
                Extents = extents;
            }

            public ObjectId EntityId { get; }

            public IReadOnlyList<Point2d> Vertices { get; }

            public Extents2d Extents { get; }
        }

        private readonly struct EvaluationSummary
        {
            public EvaluationSummary(IReadOnlyList<string> uniqueTexts, ISet<ObjectId> matchedEntityIds, int overlappingEntityCount)
            {
                UniqueTexts = uniqueTexts;
                MatchedEntityIds = matchedEntityIds;
                OverlappingEntityCount = overlappingEntityCount;
            }

            public IReadOnlyList<string> UniqueTexts { get; }

            public ISet<ObjectId> MatchedEntityIds { get; }

            public int OverlappingEntityCount { get; }
        }
    }
}
