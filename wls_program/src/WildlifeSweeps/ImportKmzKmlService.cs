using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class ImportKmzKmlService
    {
        private const int MaxObjectDataTableNameLength = 25;
        private const int MaxObjectDataFieldNameLength = 31;
        private static readonly string[] ImportDataMappingCandidates =
        {
            "NewObjectDataOnly",
            "NewObjectData",
            "ObjectDataOnly"
        };

        private static readonly int[] ImportDataMappingFallbackValues = { 1 };

        private static readonly string[] MapAssemblyNames =
        {
            "ManagedMapApi",
            "Autodesk.Map.Platform",
            "AcMapMgd",
            "AcMapPlatform"
        };

        private static readonly string[] FormatNameCandidates =
        {
            "KML",
            "KMZ",
            "Google KML",
            "Keyhole Markup Language (KML)",
            "Keyhole Markup Language",
            "Google Earth KML"
        };

        public void Execute(Document doc, Editor editor)
        {
            var filePath = PromptForSourcePath();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                editor.WriteMessage("\n** Cancelled **");
                return;
            }

            try
            {
                using (doc.LockDocument())
                {
                    if (!TryImportKmzKml(
                            doc,
                            filePath,
                            out var importedCount,
                            out var mappedLayerCount,
                            out var mappedColumnCount,
                            out var transformedCount,
                            out var fallbackTableCount,
                            out var fallbackRecordCount,
                            out var failureReason))
                    {
                        editor.WriteMessage($"\n{failureReason}");
                        return;
                    }

                    editor.WriteMessage($"\nImport complete. Imported {importedCount} entities.");
                    editor.WriteMessage($"\nObject Data mapped on {mappedLayerCount} layer(s) and {mappedColumnCount} column(s).");
                    editor.WriteMessage($"\nCoordinate transform applied to {transformedCount} entity(s).");
                    if (fallbackRecordCount > 0)
                    {
                        editor.WriteMessage($"\nFallback Object Data attached to {fallbackRecordCount} entity(s) across {fallbackTableCount} table(s).");
                    }
                    else if (mappedColumnCount <= 0)
                    {
                        editor.WriteMessage("\nNo KMZ/KML columns were exposed and fallback Object Data attachment did not complete.");
                    }
                }
            }
            catch (Exception ex)
            {
                var logPath = PluginLogger.TryLogException(doc, "IMPORTKMZKML", ex);
                editor.WriteMessage("\nUnexpected error during KMZ/KML import.");
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    editor.WriteMessage($"\nDetails logged to: {logPath}");
                }
            }
        }

        private static string? PromptForSourcePath()
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select KMZ/KML file",
                Filter = "Google Earth files (*.kmz;*.kml)|*.kmz;*.kml|KML (*.kml)|*.kml|KMZ (*.kmz)|*.kmz|All files (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true,
                CheckPathExists = true
            };

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? dialog.FileName
                : null;
        }

        private static bool TryImportKmzKml(
            Document doc,
            string filePath,
            out int importedCount,
            out int mappedLayerCount,
            out int mappedColumnCount,
            out int transformedCount,
            out int fallbackTableCount,
            out int fallbackRecordCount,
            out string failureReason)
        {
            importedCount = 0;
            mappedLayerCount = 0;
            mappedColumnCount = 0;
            transformedCount = 0;
            fallbackTableCount = 0;
            fallbackRecordCount = 0;
            failureReason = string.Empty;

            var mapApplication = TryGetMapApplication();
            if (mapApplication == null)
            {
                failureReason = "Map import API is unavailable. Run this in AutoCAD Map 3D/Civil 3D (2025).";
                return false;
            }

            var activeProject = GetMemberValue(mapApplication, "ActiveProject");
            if (activeProject == null)
            {
                failureReason = "No active Map project found.";
                return false;
            }

            var drawingProjection = ResolveDrawingCoordinateSystem(activeProject);
            if (string.IsNullOrWhiteSpace(drawingProjection))
            {
                failureReason = "No drawing coordinate system assigned. Run MAPCSASSIGN, then retry import.";
                return false;
            }

            var importer = GetMemberValue(mapApplication, "Importer");
            if (importer == null)
            {
                failureReason = "Map importer service is unavailable.";
                return false;
            }

            if (!TryInitializeImporter(importer, mapApplication, filePath, out var formatName))
            {
                failureReason = "Could not initialize importer for this KMZ/KML file.";
                return false;
            }

            var sourceCoordinateSystem = ResolveSourceCoordinateSystemForFile(filePath);
            ApplyObjectDataMapping(importer, drawingProjection, sourceCoordinateSystem, out mappedLayerCount, out mappedColumnCount);
            if (mappedLayerCount <= 0)
            {
                failureReason = "Could not apply input layer mappings for KMZ/KML import. Import aborted so coordinates are not brought in incorrectly.";
                return false;
            }

            TryApplyImporterTargetCoordinateSystem(importer, drawingProjection);
            TryEnableCoordinateTransformation(importer);
            var preImportHandles = CaptureModelSpaceHandles(doc.Database);

            if (!TryInvoke(importer, "Import", out var importResult, false)
                && !TryInvoke(importer, "Import", out importResult, true)
                && !TryInvoke(importer, "Import", out importResult))
            {
                failureReason = $"Importer execution failed for format '{formatName}'.";
                return false;
            }

            importedCount = ToInt(GetMemberValue(importResult, "EntitiesImported"));
            transformedCount = TryTransformImportedEntities(
                doc.Database,
                preImportHandles,
                sourceCoordinateSystem,
                drawingProjection,
                importedCount);

            if (mappedColumnCount <= 0)
            {
                fallbackRecordCount = TryAttachFallbackObjectData(
                    doc.Database,
                    activeProject,
                    preImportHandles,
                    drawingProjection,
                    filePath,
                    out fallbackTableCount);
            }

            return true;
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
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
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

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
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

            foreach (var property in type.GetProperties(flags).Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.CanWrite))
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

            foreach (var method in type.GetMethods(flags).Where(m => string.Equals(m.Name, $"set_{name}", StringComparison.OrdinalIgnoreCase)))
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

        private static bool TryInvoke(object target, string methodName, out object? result, params object?[] args)
        {
            result = null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var methods = target.GetType().GetMethods(flags)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.GetParameters().Length);

            foreach (var method in methods)
            {
                if (!TryPrepareArguments(method, args, out var invokeArgs))
                {
                    continue;
                }

                try
                {
                    result = method.Invoke(target, invokeArgs);
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
                invokeArgs = Array.Empty<object>();
                return false;
            }

            invokeArgs = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!TryConvertArgument(inputArgs[i], parameters[i].ParameterType, out var converted))
                {
                    return false;
                }

                invokeArgs[i] = converted;
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

        private static bool TryInitializeImporter(object importer, object mapApplication, string filePath, out string selectedFormat)
        {
            selectedFormat = string.Empty;
            var candidateNames = new List<string>();

            foreach (var name in ResolveFormatNames(mapApplication, filePath))
            {
                AddUnique(candidateNames, name);
            }

            var extension = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(extension))
            {
                AddUnique(candidateNames, extension);
            }

            foreach (var fallback in FormatNameCandidates)
            {
                AddUnique(candidateNames, fallback);
            }

            foreach (var name in candidateNames)
            {
                if (TryInvoke(importer, "Init", out _, name, filePath))
                {
                    selectedFormat = name;
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> ResolveFormatNames(object mapApplication, string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var formats = GetMemberValue(mapApplication, "ImportFormats");
            if (formats == null)
            {
                yield break;
            }

            foreach (var format in EnumerateCollection(formats))
            {
                var formatName = Convert.ToString(GetMemberValue(format, "FormatName"), CultureInfo.InvariantCulture)?.Trim();
                var formatExtension = Convert.ToString(GetMemberValue(format, "Extension"), CultureInfo.InvariantCulture)?.ToLowerInvariant();

                if (string.IsNullOrWhiteSpace(formatName))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(extension))
                {
                    yield return formatName;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(formatExtension) && formatExtension.Contains(extension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return formatName;
                }
            }
        }

        private static string? ResolveDrawingCoordinateSystem(object activeProject)
        {
            foreach (var candidate in EnumerateCoordinateSystemCandidates(activeProject))
            {
                var asText = Convert.ToString(candidate, CultureInfo.InvariantCulture)?.Trim();
                if (IsLikelyCoordinateSystemCode(asText))
                {
                    return asText;
                }
            }

            foreach (var candidate in EnumerateCoordinateSystemCandidates(activeProject))
            {
                var asText = Convert.ToString(candidate, CultureInfo.InvariantCulture)?.Trim();
                if (!string.IsNullOrWhiteSpace(asText) && !LooksLikeTypeName(asText))
                {
                    return asText;
                }
            }

            return null;
        }

        private static IEnumerable<object?> EnumerateCoordinateSystemCandidates(object activeProject)
        {
            var projection = GetMemberValue(activeProject, "Projection");
            yield return GetMemberValue(activeProject, "CoordinateSystem");
            yield return GetMemberValue(activeProject, "CoordSys");
            yield return GetMemberValue(activeProject, "MapCoordinateSystem");
            yield return GetMemberValue(activeProject, "CurrentCoordinateSystem");
            yield return GetMemberValue(activeProject, "DrawingCoordinateSystem");
            yield return GetMemberValue(activeProject, "ProjectionCode");

            if (projection != null)
            {
                yield return GetMemberValue(projection, "Code");
                yield return GetMemberValue(projection, "CsCode");
                yield return GetMemberValue(projection, "Name");
                yield return GetMemberValue(projection, "CoordinateSystem");
                yield return GetMemberValue(projection, "CoordSys");
            }

            yield return projection;
        }

        private static bool IsLikelyCoordinateSystemCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            if (LooksLikeTypeName(text))
            {
                return false;
            }

            if (text.IndexOf(' ') >= 0)
            {
                return false;
            }

            return text.Length <= 80;
        }

        private static bool LooksLikeTypeName(string value)
        {
            return value.IndexOf("Autodesk", StringComparison.OrdinalIgnoreCase) >= 0
                   || value.IndexOf("System.", StringComparison.OrdinalIgnoreCase) >= 0
                   || value.IndexOf("Microsoft.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? ResolveSourceCoordinateSystemForFile(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension is ".kml" or ".kmz" ? "LL84" : null;
        }

        private static bool TryApplyImporterTargetCoordinateSystem(object importer, string targetCoordinateSystem)
        {
            return TryInvoke(importer, "SetTargetCoordSys", out _, targetCoordinateSystem)
                   || TryInvoke(importer, "SetTargetCoordinateSystem", out _, targetCoordinateSystem)
                   || TryInvoke(importer, "SetOutputCoordSys", out _, targetCoordinateSystem)
                   || TryInvoke(importer, "SetOutputCoordinateSystem", out _, targetCoordinateSystem)
                   || TrySetAnyMemberValue(
                       importer,
                       targetCoordinateSystem,
                       "TargetCoordinateSystem",
                       "TargetCoordSys",
                       "OutputCoordinateSystem",
                       "OutputCoordSys");
        }

        private static void ApplyObjectDataMapping(
            object importer,
            string targetCoordinateSystem,
            string? sourceCoordinateSystem,
            out int mappedLayerCount,
            out int mappedColumnCount)
        {
            mappedLayerCount = 0;
            mappedColumnCount = 0;
            var layerIndex = 1;

            foreach (var inputLayer in EnumerateInputLayers(importer))
            {
                TryEnableInputLayerImport(inputLayer);

                var layerName = Convert.ToString(GetMemberValue(inputLayer, "Name"), CultureInfo.InvariantCulture);
                var tableName = BuildSafeName(layerName, "WLSOD", layerIndex, MaxObjectDataTableNameLength);
                if (TryConfigureObjectDataMapping(inputLayer, tableName))
                {
                    mappedLayerCount++;
                }

                try
                {
                    var columnIndex = 1;
                    foreach (var column in EnumerateLayerColumns(inputLayer))
                    {
                        var columnName = Convert.ToString(GetMemberValue(column, "ColumnName"), CultureInfo.InvariantCulture)
                                         ?? Convert.ToString(GetMemberValue(column, "Name"), CultureInfo.InvariantCulture)
                                         ?? Convert.ToString(GetMemberValue(column, "FieldName"), CultureInfo.InvariantCulture);
                        var fieldName = BuildSafeName(columnName, "FIELD", columnIndex, MaxObjectDataFieldNameLength);
                        if (TryConfigureColumnMapping(column, fieldName))
                        {
                            mappedColumnCount++;
                        }

                        columnIndex++;
                    }
                }
                catch
                {
                    // Some Map iterator wrappers can throw during enumeration; layer-level OD mapping still applies.
                }

                TryApplyTargetCoordinateSystem(inputLayer, targetCoordinateSystem);
                TryApplySourceCoordinateSystem(inputLayer, sourceCoordinateSystem);
                TryEnableCoordinateTransformation(inputLayer);

                layerIndex++;
            }
        }

        private static IEnumerable<object> EnumerateInputLayers(object importer)
        {
            var yielded = new List<object>();

            foreach (var inputLayer in EnumerateFromIteratorFactory(importer, "InputLayerIterator"))
            {
                if (TryRememberUnique(yielded, inputLayer))
                {
                    yield return inputLayer;
                }
            }

            foreach (var inputLayer in EnumerateCollection(importer))
            {
                if (TryRememberUnique(yielded, inputLayer))
                {
                    yield return inputLayer;
                }
            }

            foreach (var memberName in new[] { "InputLayers", "Layers", "LayerIterator", "LayerMappings", "InputLayerIterator" })
            {
                var source = GetMemberValue(importer, memberName);
                foreach (var inputLayer in EnumerateCollection(source))
                {
                    if (TryRememberUnique(yielded, inputLayer))
                    {
                        yield return inputLayer;
                    }
                }
            }
        }

        private static IEnumerable<object> EnumerateLayerColumns(object inputLayer)
        {
            var yielded = new List<object>();

            foreach (var column in EnumerateFromIteratorFactory(inputLayer, "ColumnIterator"))
            {
                if (TryRememberUnique(yielded, column))
                {
                    yield return column;
                }
            }

            foreach (var column in EnumerateCollection(inputLayer))
            {
                if (TryRememberUnique(yielded, column))
                {
                    yield return column;
                }
            }

            foreach (var memberName in new[] { "Columns", "ColumnIterator", "ColumnMappings", "Fields", "Attributes" })
            {
                var source = GetMemberValue(inputLayer, memberName);
                foreach (var column in EnumerateCollection(source))
                {
                    if (TryRememberUnique(yielded, column))
                    {
                        yield return column;
                    }
                }
            }
        }

        private static bool TryEnableInputLayerImport(object inputLayer)
        {
            return TryInvoke(inputLayer, "SetImportFromInputLayerOn", out _, true)
                   || TryInvoke(inputLayer, "SetImportFromInputLayer", out _, true)
                   || TryInvoke(inputLayer, "SetImportOn", out _, true)
                   || TrySetAnyMemberValue(
                       inputLayer,
                       true,
                       "ImportFromInputLayerOn",
                       "ImportFromInputLayer",
                       "ImportOn",
                       "Import");
        }

        private static bool TryApplyTargetCoordinateSystem(object inputLayer, string targetCoordinateSystem)
        {
            return TryInvoke(inputLayer, "SetTargetCoordSys", out _, targetCoordinateSystem)
                   || TryInvoke(inputLayer, "SetTargetCoordinateSystem", out _, targetCoordinateSystem)
                   || TryInvoke(inputLayer, "SetOutputCoordSys", out _, targetCoordinateSystem)
                   || TryInvoke(inputLayer, "SetOutputCoordinateSystem", out _, targetCoordinateSystem)
                   || TrySetAnyMemberValue(
                       inputLayer,
                       targetCoordinateSystem,
                       "TargetCoordinateSystem",
                       "TargetCoordSys",
                       "OutputCoordinateSystem",
                       "OutputCoordSys");
        }

        private static bool TryApplySourceCoordinateSystem(object inputLayer, string? sourceCoordinateSystem)
        {
            if (string.IsNullOrWhiteSpace(sourceCoordinateSystem))
            {
                return false;
            }

            return TryInvoke(inputLayer, "SetSourceCoordSys", out _, sourceCoordinateSystem)
                   || TryInvoke(inputLayer, "SetSourceCoordinateSystem", out _, sourceCoordinateSystem)
                   || TryInvoke(inputLayer, "SetInputCoordSys", out _, sourceCoordinateSystem)
                   || TryInvoke(inputLayer, "SetInputCoordinateSystem", out _, sourceCoordinateSystem)
                   || TrySetAnyMemberValue(
                       inputLayer,
                       sourceCoordinateSystem,
                       "SourceCoordinateSystem",
                       "SourceCoordSys",
                       "InputCoordinateSystem",
                       "InputCoordSys");
        }

        private static void TryEnableCoordinateTransformation(object target)
        {
            TryInvoke(target, "SetTransformToTargetCoordinateSystem", out _, true);
            TryInvoke(target, "SetCoordinateTransformationOn", out _, true);
            TryInvoke(target, "SetCoordinateTransformOn", out _, true);
            TryInvoke(target, "SetReprojectOn", out _, true);
            TrySetAnyMemberValue(
                target,
                true,
                "TransformToTargetCoordinateSystem",
                "CoordinateTransformationOn",
                "CoordinateTransformOn",
                "ReprojectOn",
                "DoCoordinateTransformation",
                "EnableCoordinateTransformation");
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
            var methods = inputLayer.GetType()
                .GetMethods(flags)
                .Where(m => string.Equals(m.Name, "SetDataMapping", StringComparison.OrdinalIgnoreCase));

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
                        object? enumValue = null;
                        var parsed = false;
                        try
                        {
                            enumValue = Enum.Parse(parameterType, candidate, ignoreCase: true);
                            parsed = true;
                        }
                        catch
                        {
                        }

                        if (parsed)
                        {
                            TryRememberUnique(results, enumValue);
                        }
                    }

                    foreach (var fallbackValue in ImportDataMappingFallbackValues)
                    {
                        object? enumValue = null;
                        var converted = false;
                        try
                        {
                            enumValue = Enum.ToObject(parameterType, fallbackValue);
                            converted = true;
                        }
                        catch
                        {
                        }

                        if (converted)
                        {
                            TryRememberUnique(results, enumValue);
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

        private static IEnumerable<object> EnumerateFromIteratorFactory(object source, string iteratorFactoryName)
        {
            var yielded = new List<object>();

            foreach (var iterator in ResolveIteratorCandidates(source, iteratorFactoryName))
            {
                foreach (var item in EnumerateMapIterator(iterator))
                {
                    if (TryRememberUnique(yielded, item))
                    {
                        yield return item;
                    }
                }
            }
        }

        private static IEnumerable<object> ResolveIteratorCandidates(object source, string iteratorFactoryName)
        {
            if (TryInvoke(source, iteratorFactoryName, out var returnValue) && returnValue != null)
            {
                yield return returnValue;
            }

            if (TryInvokeOutParameter(source, iteratorFactoryName, out var outValue) && outValue != null)
            {
                yield return outValue;
            }

            var memberValue = GetMemberValue(source, iteratorFactoryName);
            if (memberValue != null)
            {
                yield return memberValue;
            }
        }

        private static IEnumerable<object> EnumerateMapIterator(object iterator)
        {
            if (iterator == null)
            {
                yield break;
            }

            var yielded = new List<object>();
            ResetMapIterator(iterator);
            for (var guard = 0; guard < 10000; guard++)
            {
                if (IsMapIteratorDone(iterator))
                {
                    break;
                }

                if (TryReadIteratorItem(iterator, out var currentItem)
                    && TryRememberUnique(yielded, currentItem))
                {
                    yield return currentItem!;
                }

                if (!TryStepMapIterator(iterator))
                {
                    break;
                }
            }

            if (yielded.Count > 0)
            {
                yield break;
            }

            foreach (var item in EnumerateCollection(iterator))
            {
                if (TryRememberUnique(yielded, item))
                {
                    yield return item;
                }
            }
        }

        private static void ResetMapIterator(object iterator)
        {
            if (!TryInvoke(iterator, "Rewind", out _))
            {
                TryInvoke(iterator, "Reset", out _);
            }
        }

        private static bool IsMapIteratorDone(object iterator)
        {
            var doneValue = GetMemberValue(iterator, "Done");
            if (doneValue != null)
            {
                return ToBool(doneValue);
            }

            if (TryInvoke(iterator, "Done", out var doneResult))
            {
                return ToBool(doneResult);
            }

            return false;
        }

        private static bool TryReadIteratorItem(object iterator, out object? currentItem)
        {
            currentItem = GetMemberValue(iterator, "Current")
                          ?? GetMemberValue(iterator, "Item")
                          ?? GetMemberValue(iterator, "Value");

            if (currentItem != null)
            {
                return true;
            }

            if (TryInvoke(iterator, "Current", out currentItem) && currentItem != null)
            {
                return true;
            }

            if (TryInvoke(iterator, "GetCurrent", out currentItem) && currentItem != null)
            {
                return true;
            }

            if (TryInvoke(iterator, "Get", out currentItem) && currentItem != null)
            {
                return true;
            }

            return TryInvokeOutParameter(iterator, "Get", out currentItem) && currentItem != null;
        }

        private static bool TryStepMapIterator(object iterator)
        {
            return TryInvoke(iterator, "Step", out _)
                   || TryInvoke(iterator, "MoveNext", out _)
                   || TryInvoke(iterator, "Next", out _)
                   || TryInvoke(iterator, "MoveNext", out _, true);
        }

        private static bool TryInvokeOutParameter(object target, string methodName, out object? outValue)
        {
            outValue = null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var methods = target.GetType().GetMethods(flags)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsByRef)
                {
                    continue;
                }

                var args = new object?[] { null };
                try
                {
                    var invokeResult = method.Invoke(target, args);
                    var success = invokeResult == null || ToBool(invokeResult);
                    if (!success)
                    {
                        continue;
                    }

                    if (args[0] != null)
                    {
                        outValue = args[0];
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static IEnumerable<object> EnumerateCollection(object? source)
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

                if (enumerator == null)
                {
                    yield break;
                }

                var yielded = new List<object>();
                var failed = false;
                while (true)
                {
                    bool movedNext;
                    try
                    {
                        movedNext = enumerator.MoveNext();
                    }
                    catch
                    {
                        failed = true;
                        break;
                    }

                    if (!movedNext)
                    {
                        break;
                    }

                    object? current;
                    try
                    {
                        current = enumerator.Current;
                    }
                    catch
                    {
                        failed = true;
                        break;
                    }

                    if (TryRememberUnique(yielded, current))
                    {
                        yield return current!;
                    }
                }

                if (failed || (yielded.Count == 0 && ShouldPreferIteratorFallback(enumerator)))
                {
                    foreach (var item in EnumerateIteratorFallback(enumerator))
                    {
                        if (TryRememberUnique(yielded, item))
                        {
                            yield return item;
                        }
                    }
                }

                yield break;
            }

            var getEnumerator = source.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getEnumerator != null)
            {
                object? iterator;
                try
                {
                    iterator = getEnumerator.Invoke(source, Array.Empty<object>());
                }
                catch
                {
                    yield break;
                }

                if (iterator is IEnumerator e)
                {
                    var yielded = new List<object>();
                    var failed = false;
                    while (true)
                    {
                        bool movedNext;
                        try
                        {
                            movedNext = e.MoveNext();
                        }
                        catch
                        {
                            failed = true;
                            break;
                        }

                        if (!movedNext)
                        {
                            break;
                        }

                        object? current;
                        try
                        {
                            current = e.Current;
                        }
                        catch
                        {
                            failed = true;
                            break;
                        }

                        if (TryRememberUnique(yielded, current))
                        {
                            yield return current!;
                        }
                    }

                    if (failed || (yielded.Count == 0 && ShouldPreferIteratorFallback(e)))
                    {
                        foreach (var item in EnumerateIteratorFallback(e))
                        {
                            if (TryRememberUnique(yielded, item))
                            {
                                yield return item;
                            }
                        }
                    }

                    yield break;
                }

                foreach (var item in EnumerateIteratorFallback(iterator))
                {
                    yield return item;
                }

                yield break;
            }

            var count = ToInt(GetMemberValue(source, "Count"));
            for (var i = 0; i < count; i++)
            {
                if (TryGetIndexItem(source, i, out var item) && item != null)
                {
                    yield return item;
                }
            }
        }

        private static bool ShouldPreferIteratorFallback(object iterator)
        {
            var type = iterator.GetType();
            var fullName = type.FullName ?? type.Name;
            return fullName.IndexOf("Autodesk.Gis.Map", StringComparison.OrdinalIgnoreCase) >= 0
                   && fullName.IndexOf("Iterator", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<object> EnumerateIteratorFallback(object? iterator)
        {
            if (iterator == null)
            {
                yield break;
            }

            var emittedAny = false;
            foreach (var item in EnumerateIteratorFallbackCore(iterator, readCurrentBeforeMove: true))
            {
                emittedAny = true;
                yield return item;
            }

            if (emittedAny)
            {
                yield break;
            }

            foreach (var item in EnumerateIteratorFallbackCore(iterator, readCurrentBeforeMove: false))
            {
                yield return item;
            }
        }

        private static IEnumerable<object> EnumerateIteratorFallbackCore(object iterator, bool readCurrentBeforeMove)
        {
            ResetIterator(iterator);
            object? lastEmitted = null;

            for (var guard = 0; guard < 10000; guard++)
            {
                if (readCurrentBeforeMove && TryReadCurrent(iterator, out var currentBeforeMove)
                    && !IsSameItem(currentBeforeMove, lastEmitted))
                {
                    lastEmitted = currentBeforeMove;
                    yield return currentBeforeMove!;
                }

                var moved = TryAdvanceIterator(iterator);

                if (!readCurrentBeforeMove && TryReadCurrent(iterator, out var currentAfterMove)
                    && !IsSameItem(currentAfterMove, lastEmitted))
                {
                    lastEmitted = currentAfterMove;
                    yield return currentAfterMove!;
                }

                if (!moved || ToBool(GetMemberValue(iterator, "Done")))
                {
                    yield break;
                }
            }
        }

        private static void ResetIterator(object iterator)
        {
            var reset = iterator.GetType().GetMethod("Reset", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            try
            {
                reset?.Invoke(iterator, Array.Empty<object>());
            }
            catch
            {
            }
        }

        private static bool TryAdvanceIterator(object iterator)
        {
            return TryInvoke(iterator, "MoveNext", out _)
                   || TryInvoke(iterator, "Step", out _)
                   || TryInvoke(iterator, "MoveNext", out _, true);
        }

        private static bool TryReadCurrent(object iterator, out object? current)
        {
            current = GetMemberValue(iterator, "Current");
            return current != null;
        }

        private static bool IsSameItem(object current, object? previous)
        {
            if (previous == null)
            {
                return false;
            }

            try
            {
                return Equals(current, previous);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRememberUnique(ICollection<object> seen, object? candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            foreach (var existing in seen)
            {
                if (IsSameItem(candidate, existing))
                {
                    return false;
                }
            }

            seen.Add(candidate);
            return true;
        }

        private static bool TryGetIndexItem(object source, int index, out object? value)
        {
            value = null;
            var properties = source.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => string.Equals(p.Name, "Item", StringComparison.OrdinalIgnoreCase));

            foreach (var property in properties)
            {
                var indexers = property.GetIndexParameters();
                if (indexers.Length != 1)
                {
                    continue;
                }

                if (!TryConvertArgument(index, indexers[0].ParameterType, out var convertedIndex))
                {
                    continue;
                }

                try
                {
                    value = property.GetValue(source, new[] { convertedIndex });
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        private static int TryAttachFallbackObjectData(
            Database database,
            object activeProject,
            ISet<long> preImportHandles,
            string targetCoordinateSystem,
            string sourceFilePath,
            out int tableCount)
        {
            tableCount = 0;
            var importedEntities = CollectImportedEntities(database, preImportHandles);
            if (importedEntities.Count <= 0)
            {
                return 0;
            }

            var odTables = GetMemberValue(activeProject, "ODTables");
            if (odTables == null)
            {
                return 0;
            }

            var placemarks = TryLoadPlacemarkData(sourceFilePath);
            var placemarkByEntityId = BuildPlacemarkAssignments(importedEntities, placemarks, targetCoordinateSystem);
            var fieldInfos = BuildFallbackFieldInfos(placemarks);
            if (fieldInfos.Count <= 0)
            {
                return 0;
            }

            var tableName = ResolveAvailableFallbackTableName(odTables, Path.GetFileNameWithoutExtension(sourceFilePath));
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return 0;
            }

            var fieldDefinitions = CreateFallbackOdFieldDefinitions(fieldInfos);
            if (fieldDefinitions == null)
            {
                return 0;
            }

            if (!TryInvoke(odTables, "Add", out _, tableName, fieldDefinitions, "Wildlife Sweeps KMZ/KML import", false))
            {
                return 0;
            }

            if (!TryGetOdTableByName(odTables, tableName, out var table) || table == null)
            {
                return 0;
            }

            var attachedCount = 0;
            var importTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            foreach (var imported in importedEntities)
            {
                var layerKey = string.IsNullOrWhiteSpace(imported.LayerName) ? "LAYER" : imported.LayerName;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (placemarkByEntityId.TryGetValue(imported.ObjectId, out var placemark))
                {
                    foreach (var pair in placemark.Attributes)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }

                values["SRC_FILE"] = sourceFilePath;
                values["SRC_LAYER"] = layerKey;
                values["SRC_HANDLE"] = imported.HandleText;
                values["IMPORT_TS"] = importTimestamp;

                if (!TryCreateFallbackOdRecord(table, activeProject, out var record) || record == null)
                {
                    continue;
                }

                for (var i = 0; i < fieldInfos.Count; i++)
                {
                    var sourceKey = fieldInfos[i].SourceKey;
                    values.TryGetValue(sourceKey, out var fieldValue);
                    TryAssignRecordStringValue(record, i, fieldValue ?? string.Empty);
                }

                if (TryInvoke(table, "AddRecord", out _, record, imported.ObjectId))
                {
                    attachedCount++;
                }
            }

            tableCount = attachedCount > 0 ? 1 : 0;
            return attachedCount;
        }

        private static IReadOnlyList<ParsedPlacemarkData> TryLoadPlacemarkData(string sourceFilePath)
        {
            if (!TryLoadKmlDocument(sourceFilePath, out var document) || document == null)
            {
                return Array.Empty<ParsedPlacemarkData>();
            }

            var placemarks = new List<ParsedPlacemarkData>();
            foreach (var placemarkElement in document.Descendants().Where(e => string.Equals(e.Name.LocalName, "Placemark", StringComparison.OrdinalIgnoreCase)))
            {
                var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var name = TryGetElementValue(placemarkElement, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    attributes["NAME"] = name!;
                }

                var description = TryGetElementValue(placemarkElement, "description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    attributes["DESCRIPTION"] = description!;
                }

                var address = TryGetElementValue(placemarkElement, "address");
                if (!string.IsNullOrWhiteSpace(address))
                {
                    attributes["ADDRESS"] = address!;
                }

                foreach (var dataElement in placemarkElement.Descendants().Where(e => string.Equals(e.Name.LocalName, "Data", StringComparison.OrdinalIgnoreCase)))
                {
                    var key = NormalizeAttributeValue(dataElement.Attribute("name")?.Value);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var valueElement = dataElement.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "value", StringComparison.OrdinalIgnoreCase));
                    var value = NormalizeAttributeValue(valueElement?.Value ?? dataElement.Value);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    attributes[key] = value;
                }

                foreach (var simpleDataElement in placemarkElement.Descendants().Where(e => string.Equals(e.Name.LocalName, "SimpleData", StringComparison.OrdinalIgnoreCase)))
                {
                    var key = NormalizeAttributeValue(simpleDataElement.Attribute("name")?.Value);
                    var value = NormalizeAttributeValue(simpleDataElement.Value);
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    attributes[key] = value;
                }

                Point3d? sourcePoint = null;
                var coordinatesText = placemarkElement.Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "coordinates", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (TryParseKmlCoordinates(coordinatesText, out var lon, out var lat))
                {
                    sourcePoint = new Point3d(lon, lat, 0.0);
                }
                else
                {
                    var gxCoordText = placemarkElement.Descendants()
                        .FirstOrDefault(e => string.Equals(e.Name.LocalName, "coord", StringComparison.OrdinalIgnoreCase))
                        ?.Value;
                    if (TryParseGxCoordinate(gxCoordText, out lon, out lat))
                    {
                        sourcePoint = new Point3d(lon, lat, 0.0);
                    }
                }

                if (attributes.Count > 0 || sourcePoint != null)
                {
                    placemarks.Add(new ParsedPlacemarkData(attributes, sourcePoint));
                }
            }

            return placemarks;
        }

        private static bool TryLoadKmlDocument(string sourceFilePath, out XDocument? document)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                return false;
            }

            try
            {
                var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
                if (extension == ".kml")
                {
                    using var stream = File.OpenRead(sourceFilePath);
                    document = XDocument.Load(stream);
                    return true;
                }

                if (extension != ".kmz")
                {
                    return false;
                }

                using var archive = ZipFile.OpenRead(sourceFilePath);
                var kmlEntry = archive.Entries
                    .Where(e => string.Equals(Path.GetExtension(e.FullName), ".kml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => string.Equals(Path.GetFileName(e.FullName), "doc.kml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(e => e.FullName.Length)
                    .FirstOrDefault();
                if (kmlEntry == null)
                {
                    return false;
                }

                using var entryStream = kmlEntry.Open();
                document = XDocument.Load(entryStream);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetOdTableByName(object odTables, string tableName, out object? table)
        {
            table = null;
            return (TryInvoke(odTables, "get_Item", out table, tableName) || TryInvoke(odTables, "Item", out table, tableName))
                   && table != null;
        }

        private static IReadOnlyDictionary<ObjectId, ParsedPlacemarkData> BuildPlacemarkAssignments(
            IReadOnlyList<ImportedEntityInfo> importedEntities,
            IReadOnlyList<ParsedPlacemarkData> placemarks,
            string targetCoordinateSystem)
        {
            var assignments = new Dictionary<ObjectId, ParsedPlacemarkData>();
            if (importedEntities.Count <= 0 || placemarks.Count <= 0)
            {
                return assignments;
            }

            Map3dCoordinateTransformer? mapTransformer = null;
            TryCreateImportTransformer("LL84", targetCoordinateSystem, out mapTransformer);
            TryCreateUtmFallbackConverter(targetCoordinateSystem, out var utmFallbackConverter);

            var remainingEntityIndexes = new HashSet<int>(Enumerable.Range(0, importedEntities.Count));
            var unmappedPlacemarks = new List<ParsedPlacemarkData>();

            foreach (var placemark in placemarks)
            {
                if (placemark.SourcePoint == null
                    || !TryTransformPoint(placemark.SourcePoint.Value, mapTransformer, utmFallbackConverter, out var projectedPoint))
                {
                    unmappedPlacemarks.Add(placemark);
                    continue;
                }

                var bestEntityIndex = -1;
                var bestDistance = double.MaxValue;
                foreach (var entityIndex in remainingEntityIndexes)
                {
                    var entity = importedEntities[entityIndex];
                    if (!entity.HasLocation)
                    {
                        continue;
                    }

                    var distance = DistanceSquared(projectedPoint, entity.Location);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestEntityIndex = entityIndex;
                    }
                }

                if (bestEntityIndex < 0)
                {
                    unmappedPlacemarks.Add(placemark);
                    continue;
                }

                assignments[importedEntities[bestEntityIndex].ObjectId] = placemark;
                remainingEntityIndexes.Remove(bestEntityIndex);
            }

            if (unmappedPlacemarks.Count <= 0 || remainingEntityIndexes.Count <= 0)
            {
                return assignments;
            }

            var remainingEntities = remainingEntityIndexes
                .Select(index => importedEntities[index])
                .OrderBy(entity => entity.ObjectId.Handle.Value)
                .ToList();
            var count = Math.Min(remainingEntities.Count, unmappedPlacemarks.Count);
            for (var i = 0; i < count; i++)
            {
                assignments[remainingEntities[i].ObjectId] = unmappedPlacemarks[i];
            }

            return assignments;
        }

        private static double DistanceSquared(Point3d first, Point3d second)
        {
            var dx = first.X - second.X;
            var dy = first.Y - second.Y;
            var dz = first.Z - second.Z;
            return (dx * dx) + (dy * dy) + (dz * dz);
        }

        private static IReadOnlyList<OdFieldInfo> BuildFallbackFieldInfos(IReadOnlyList<ParsedPlacemarkData> placemarks)
        {
            var orderedKeys = new List<string>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var placemark in placemarks)
            {
                foreach (var key in placemark.Attributes.Keys)
                {
                    if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key))
                    {
                        continue;
                    }

                    orderedKeys.Add(key);
                }
            }

            foreach (var metadataKey in new[] { "SRC_FILE", "SRC_LAYER", "SRC_HANDLE", "IMPORT_TS" })
            {
                if (seenKeys.Add(metadataKey))
                {
                    orderedKeys.Add(metadataKey);
                }
            }

            var fieldInfos = new List<OdFieldInfo>(orderedKeys.Count);
            var usedFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < orderedKeys.Count; i++)
            {
                var sourceKey = orderedKeys[i];
                var fieldName = BuildSafeFieldName(sourceKey, i + 1, usedFieldNames);
                fieldInfos.Add(new OdFieldInfo(sourceKey, fieldName));
            }

            return fieldInfos;
        }

        private static string BuildSafeFieldName(string sourceKey, int index, ISet<string> usedFieldNames)
        {
            var cleaned = new StringBuilder();
            var source = NormalizeAttributeValue(sourceKey);
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

            if (usedFieldNames.Add(candidate))
            {
                return candidate;
            }

            for (var suffixIndex = 1; suffixIndex < 1000; suffixIndex++)
            {
                var suffix = "_" + suffixIndex.ToString(CultureInfo.InvariantCulture);
                var budget = Math.Max(1, MaxObjectDataFieldNameLength - suffix.Length);
                var trimmed = candidate.Length > budget
                    ? candidate.Substring(0, budget)
                    : candidate;
                var withSuffix = trimmed + suffix;
                if (usedFieldNames.Add(withSuffix))
                {
                    return withSuffix;
                }
            }

            return candidate;
        }

        private static string ResolveAvailableFallbackTableName(object odTables, string? fileNameWithoutExtension)
        {
            var baseName = string.IsNullOrWhiteSpace(fileNameWithoutExtension)
                ? "WLSKMZ"
                : fileNameWithoutExtension!;

            for (var i = 1; i < 1000; i++)
            {
                var candidate = BuildSafeName(baseName, "WLSKMZ", i, MaxObjectDataTableNameLength);
                if (!TryInvoke(odTables, "IsTableDefined", out var definedResult, candidate)
                    || !ToBool(definedResult))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static object? CreateFallbackOdFieldDefinitions(IReadOnlyList<OdFieldInfo> fieldInfos)
        {
            var definitions = CreateObjectDataFieldDefinitions();
            if (definitions == null)
            {
                return null;
            }

            var position = 0;
            foreach (var fieldInfo in fieldInfos)
            {
                var definition = CreateObjectDataFieldDefinition(fieldInfo.FieldName, fieldInfo.SourceKey);
                if (definition == null)
                {
                    continue;
                }

                if (TryInvoke(definitions, "AddColumn", out _, definition, position))
                {
                    position++;
                }
            }

            return position > 0 ? definitions : null;
        }

        private static object? CreateObjectDataFieldDefinitions()
        {
            var type = FindType("Autodesk.Gis.Map.ObjectData.FieldDefinitions");
            if (type == null)
            {
                return null;
            }

            try
            {
                var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                return create?.Invoke(null, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static object? CreateObjectDataFieldDefinition(string name, string description)
        {
            var type = FindType("Autodesk.Gis.Map.ObjectData.FieldDefinition");
            if (type == null)
            {
                return null;
            }

            try
            {
                var create = type.GetMethod(
                    "Create",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string), typeof(string) },
                    null);
                return create?.Invoke(null, new object?[] { name, description, string.Empty });
            }
            catch
            {
                return null;
            }
        }

        private static bool TryCreateFallbackOdRecord(object table, object activeProject, out object? record)
        {
            record = CreateObjectDataRecord();
            if (record == null)
            {
                return false;
            }

            TrySetMemberValue(record, "Project", activeProject);
            if (!TryInvoke(table, "InitRecord", out _, record))
            {
                return false;
            }

            return true;
        }

        private static object? CreateObjectDataRecord()
        {
            var type = FindType("Autodesk.Gis.Map.ObjectData.Record");
            if (type == null)
            {
                return null;
            }

            try
            {
                var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                return create?.Invoke(null, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static void TryAssignRecordStringValue(object record, int index, string value)
        {
            if (!TryInvoke(record, "get_Item", out var mapValue, index) || mapValue == null)
            {
                return;
            }

            TryInvoke(mapValue, "Assign", out _, value ?? string.Empty);
        }

        private static bool TryParseKmlCoordinates(string? coordinatesText, out double lon, out double lat)
        {
            lon = 0.0;
            lat = 0.0;
            var text = NormalizeAttributeValue(coordinatesText);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var firstCoordinate = text
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstCoordinate))
            {
                return false;
            }

            var components = firstCoordinate.Split(',');
            if (components.Length < 2)
            {
                return false;
            }

            return double.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                   && double.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
        }

        private static bool TryParseGxCoordinate(string? gxCoordinateText, out double lon, out double lat)
        {
            lon = 0.0;
            lat = 0.0;
            var text = NormalizeAttributeValue(gxCoordinateText);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var components = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (components.Length < 2)
            {
                return false;
            }

            return double.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
                   && double.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
        }

        private static string? TryGetElementValue(XElement parent, string localName)
        {
            var element = parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
            if (element == null)
            {
                return null;
            }

            var normalized = NormalizeAttributeValue(element.Value);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string NormalizeAttributeValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
        }

        private static List<ImportedEntityInfo> CollectImportedEntities(Database database, ISet<long> preImportHandles)
        {
            var imported = new List<ImportedEntityInfo>();

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

                    var layerName = string.Empty;
                    var hasLocation = false;
                    var location = Point3d.Origin;
                    if (transaction.GetObject(entityId, OpenMode.ForRead, false) is Entity entity
                        && !string.IsNullOrWhiteSpace(entity.Layer))
                    {
                        layerName = entity.Layer;
                        hasLocation = TryGetRepresentativePoint(entity, transaction, out location);
                    }

                    imported.Add(new ImportedEntityInfo(entityId, layerName, entityId.Handle.ToString(), location, hasLocation));
                }
            }
            catch
            {
            }

            return imported;
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

        private static int TryTransformImportedEntities(
            Database database,
            ISet<long> preImportHandles,
            string? preferredSourceCoordinateSystem,
            string targetCoordinateSystem,
            int importedCount)
        {
            if (importedCount <= 0 || string.IsNullOrWhiteSpace(targetCoordinateSystem))
            {
                return 0;
            }

            Map3dCoordinateTransformer? mapTransformer = null;
            TryCreateImportTransformer(preferredSourceCoordinateSystem, targetCoordinateSystem, out mapTransformer);
            TryCreateUtmFallbackConverter(targetCoordinateSystem, out var utmFallbackConverter);

            if (mapTransformer == null && utmFallbackConverter == null)
            {
                return 0;
            }

            var transformedCount = 0;
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

                    if (transaction.GetObject(entityId, OpenMode.ForWrite, false) is not Entity entity)
                    {
                        continue;
                    }

                    if (!EntityLooksGeographic(entity, transaction))
                    {
                        continue;
                    }

                    if (TryTransformEntityGeometry(entity, transaction, mapTransformer, utmFallbackConverter))
                    {
                        transformedCount++;
                    }
                }

                transaction.Commit();
            }
            catch
            {
                return transformedCount;
            }

            return transformedCount;
        }

        private static bool TryCreateImportTransformer(
            string? preferredSourceCoordinateSystem,
            string targetCoordinateSystem,
            out Map3dCoordinateTransformer? transformer)
        {
            transformer = null;
            var candidates = new List<string>();
            AddUnique(candidates, preferredSourceCoordinateSystem);
            AddUnique(candidates, "LL83");
            AddUnique(candidates, "LL84");

            foreach (var sourceCandidate in candidates)
            {
                if (string.Equals(sourceCandidate, targetCoordinateSystem, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Map3dCoordinateTransformer.TryCreate(sourceCandidate, targetCoordinateSystem, out transformer)
                    && transformer != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryCreateUtmFallbackConverter(string targetCoordinateSystem, out UtmCoordinateConverter? converter)
        {
            converter = null;
            if (string.IsNullOrWhiteSpace(targetCoordinateSystem))
            {
                return false;
            }

            var dashIndex = targetCoordinateSystem.LastIndexOf('-');
            if (dashIndex < 0 || dashIndex >= targetCoordinateSystem.Length - 1)
            {
                return false;
            }

            var zoneText = targetCoordinateSystem.Substring(dashIndex + 1).Trim();
            if (zoneText.Length == 1)
            {
                zoneText = "0" + zoneText;
            }

            return UtmCoordinateConverter.TryCreate(zoneText, out converter);
        }

        private static bool EntityLooksGeographic(Entity entity, Transaction transaction)
        {
            if (!TryGetRepresentativePoint(entity, transaction, out var samplePoint))
            {
                return false;
            }

            return Math.Abs(samplePoint.X) <= 180.0
                   && Math.Abs(samplePoint.Y) <= 90.0;
        }

        private static bool TryGetRepresentativePoint(Entity entity, Transaction transaction, out Point3d point)
        {
            switch (entity)
            {
                case DBPoint dbPoint:
                    point = dbPoint.Position;
                    return true;
                case Line line:
                    point = line.StartPoint;
                    return true;
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
                case BlockReference blockReference:
                    point = blockReference.Position;
                    return true;
                case DBText dbText:
                    point = dbText.Position;
                    return true;
                case MText mText:
                    point = mText.Location;
                    return true;
            }

            point = Point3d.Origin;
            return false;
        }

        private static bool TryTransformEntityGeometry(
            Entity entity,
            Transaction transaction,
            Map3dCoordinateTransformer? mapTransformer,
            UtmCoordinateConverter? utmFallbackConverter)
        {
            switch (entity)
            {
                case DBPoint dbPoint:
                    if (!TryTransformPoint(dbPoint.Position, mapTransformer, utmFallbackConverter, out var dbPointPosition))
                    {
                        return false;
                    }

                    dbPoint.Position = dbPointPosition;
                    return true;

                case Line line:
                    if (!TryTransformPoint(line.StartPoint, mapTransformer, utmFallbackConverter, out var lineStart)
                        || !TryTransformPoint(line.EndPoint, mapTransformer, utmFallbackConverter, out var lineEnd))
                    {
                        return false;
                    }

                    line.StartPoint = lineStart;
                    line.EndPoint = lineEnd;
                    return true;

                case Polyline polyline:
                    var transformedVertexCount = 0;
                    for (var i = 0; i < polyline.NumberOfVertices; i++)
                    {
                        var sourcePoint = polyline.GetPoint3dAt(i);
                        if (!TryTransformPoint(sourcePoint, mapTransformer, utmFallbackConverter, out var targetPoint))
                        {
                            continue;
                        }

                        polyline.SetPointAt(i, new Point2d(targetPoint.X, targetPoint.Y));
                        transformedVertexCount++;
                    }

                    return transformedVertexCount > 0;

                case Polyline2d polyline2d:
                    var transformed2dCount = 0;
                    foreach (ObjectId vertexId in polyline2d)
                    {
                        if (transaction.GetObject(vertexId, OpenMode.ForWrite, false) is not Vertex2d vertex2d)
                        {
                            continue;
                        }

                        if (!TryTransformPoint(vertex2d.Position, mapTransformer, utmFallbackConverter, out var targetPoint))
                        {
                            continue;
                        }

                        vertex2d.Position = targetPoint;
                        transformed2dCount++;
                    }

                    return transformed2dCount > 0;

                case Polyline3d polyline3d:
                    var transformed3dCount = 0;
                    foreach (ObjectId vertexId in polyline3d)
                    {
                        if (transaction.GetObject(vertexId, OpenMode.ForWrite, false) is not PolylineVertex3d vertex3d)
                        {
                            continue;
                        }

                        if (!TryTransformPoint(vertex3d.Position, mapTransformer, utmFallbackConverter, out var targetPoint))
                        {
                            continue;
                        }

                        vertex3d.Position = targetPoint;
                        transformed3dCount++;
                    }

                    return transformed3dCount > 0;

                case BlockReference blockReference:
                    if (!TryTransformPoint(blockReference.Position, mapTransformer, utmFallbackConverter, out var blockPosition))
                    {
                        return false;
                    }

                    blockReference.Position = blockPosition;
                    return true;

                case DBText dbText:
                    if (!TryTransformPoint(dbText.Position, mapTransformer, utmFallbackConverter, out var textPosition))
                    {
                        return false;
                    }

                    dbText.Position = textPosition;

                    try
                    {
                        if (!dbText.IsDefaultAlignment
                            && TryTransformPoint(dbText.AlignmentPoint, mapTransformer, utmFallbackConverter, out var alignmentPoint))
                        {
                            dbText.AlignmentPoint = alignmentPoint;
                        }
                    }
                    catch
                    {
                    }

                    return true;

                case MText mText:
                    if (!TryTransformPoint(mText.Location, mapTransformer, utmFallbackConverter, out var location))
                    {
                        return false;
                    }

                    mText.Location = location;
                    return true;
            }

            return false;
        }

        private static bool TryTransformPoint(
            Point3d sourcePoint,
            Map3dCoordinateTransformer? mapTransformer,
            UtmCoordinateConverter? utmFallbackConverter,
            out Point3d targetPoint)
        {
            if (mapTransformer != null
                && mapTransformer.TryProject(sourcePoint, out var projectedY, out var projectedX)
                && IsFinite(projectedX)
                && IsFinite(projectedY))
            {
                targetPoint = new Point3d(projectedX, projectedY, sourcePoint.Z);
                return true;
            }

            if (utmFallbackConverter != null
                && utmFallbackConverter.TryProjectLatLon(sourcePoint.Y, sourcePoint.X, out var easting, out var northing)
                && IsFinite(easting)
                && IsFinite(northing))
            {
                targetPoint = new Point3d(easting, northing, sourcePoint.Z);
                return true;
            }

            targetPoint = sourcePoint;
            return false;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static BlockTableRecord GetModelSpaceRecord(Database database, Transaction transaction, OpenMode mode)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], mode);
        }

        private readonly struct ImportedEntityInfo
        {
            public ImportedEntityInfo(ObjectId objectId, string layerName, string handleText, Point3d location, bool hasLocation)
            {
                ObjectId = objectId;
                LayerName = layerName;
                HandleText = handleText;
                Location = location;
                HasLocation = hasLocation;
            }

            public ObjectId ObjectId { get; }

            public string LayerName { get; }

            public string HandleText { get; }

            public Point3d Location { get; }

            public bool HasLocation { get; }
        }

        private readonly struct OdFieldInfo
        {
            public OdFieldInfo(string sourceKey, string fieldName)
            {
                SourceKey = sourceKey;
                FieldName = fieldName;
            }

            public string SourceKey { get; }

            public string FieldName { get; }
        }

        private sealed class ParsedPlacemarkData
        {
            public ParsedPlacemarkData(IReadOnlyDictionary<string, string> attributes, Point3d? sourcePoint)
            {
                Attributes = attributes;
                SourcePoint = sourcePoint;
            }

            public IReadOnlyDictionary<string, string> Attributes { get; }

            public Point3d? SourcePoint { get; }
        }

        private static void AddUnique(ICollection<string> target, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }

        private static string BuildSafeName(string? rawName, string fallbackPrefix, int index, int maxLength)
        {
            var cleaned = new StringBuilder();
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

            var baseName = cleaned.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = fallbackPrefix;
            }

            if (!char.IsLetter(baseName[0]))
            {
                baseName = fallbackPrefix + "_" + baseName;
            }

            var suffix = "_" + index.ToString(CultureInfo.InvariantCulture);
            var budget = Math.Max(1, maxLength - suffix.Length);
            if (baseName.Length > budget)
            {
                baseName = baseName.Substring(0, budget).Trim('_');
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = fallbackPrefix;
                }
                if (!char.IsLetter(baseName[0]))
                {
                    baseName = "F" + baseName.Substring(Math.Min(1, baseName.Length));
                }
                if (baseName.Length > budget)
                {
                    baseName = baseName.Substring(0, budget);
                }
            }

            return baseName + suffix;
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
    }
}
