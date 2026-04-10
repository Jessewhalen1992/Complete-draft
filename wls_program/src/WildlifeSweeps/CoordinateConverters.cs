using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class Map3dCoordinateTransformer
    {
        private static readonly object MapAssemblySync = new();
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        [ThreadStatic]
        private static HashSet<string>? _resolvingAssemblyNames;

        private readonly object? _transform;
        private readonly MethodInfo? _transformMethod;
        private readonly Func<Point3d, ProjectionResult>? _projector;
        private readonly TransformSignature _signature;
        private static string[] _mapAssemblySearchDirectories = Array.Empty<string>();
        private static bool _mapAssemblyResolverInstalled;

        private Map3dCoordinateTransformer(object transform, MethodInfo transformMethod, TransformSignature signature)
        {
            _transform = transform;
            _transformMethod = transformMethod;
            _signature = signature;
        }

        private Map3dCoordinateTransformer(Func<Point3d, ProjectionResult> projector)
        {
            _projector = projector;
            _signature = TransformSignature.Unknown;
        }

        public static bool TryCreate(string sourceCode, string destCode, out Map3dCoordinateTransformer? transformer)
        {
            transformer = null;
            EnsureMapAssembliesPrepared();

            if (TryCreateLegacy(sourceCode, destCode, out transformer))
            {
                return true;
            }

            if (TryCreateModern(sourceCode, destCode, out transformer))
            {
                return true;
            }

            if (TryCreateMapGuide(sourceCode, destCode, out transformer))
            {
                return true;
            }

            return false;
        }

        public bool TryProject(Point3d point, out double lat, out double lon)
        {
            lat = 0.0;
            lon = 0.0;

            if (_projector != null)
            {
                var result = _projector(point);
                lat = result.Latitude;
                lon = result.Longitude;
                return result.Success;
            }

            try
            {
                switch (_signature)
                {
                    case TransformSignature.TwoDoubles:
                    {
                        var result = _transformMethod!.Invoke(_transform, new object[] { point.X, point.Y });
                        return TryReadResult(result, out lat, out lon);
                    }
                    case TransformSignature.RefDoubles:
                    {
                        var args = new object[] { point.X, point.Y };
                        _transformMethod!.Invoke(_transform, args);
                        lon = Convert.ToDouble(args[0], CultureInfo.InvariantCulture);
                        lat = Convert.ToDouble(args[1], CultureInfo.InvariantCulture);
                        return true;
                    }
                    case TransformSignature.DoubleArray:
                    {
                        var args = new[] { point.X, point.Y };
                        var result = _transformMethod!.Invoke(_transform, new object[] { args });
                        if (TryReadResult(result, out lat, out lon))
                        {
                            return true;
                        }

                        if (args.Length >= 2)
                        {
                            lon = args[0];
                            lat = args[1];
                            return true;
                        }

                        return false;
                    }
                    case TransformSignature.SingleObject:
                    {
                        var parameterType = _transformMethod!.GetParameters()[0].ParameterType;
                        var argument = CreatePointInstance(parameterType, point.X, point.Y);
                        if (argument == null)
                        {
                            return false;
                        }

                        var result = _transformMethod.Invoke(_transform, new[] { argument });
                        if (TryReadResult(result, out lat, out lon))
                        {
                            return true;
                        }

                        return TryReadResult(argument, out lat, out lon);
                    }
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateLegacy(string sourceCode, string destCode, out Map3dCoordinateTransformer? transformer)
        {
            transformer = null;
            var factoryType = FindLegacyFactoryType();
            if (factoryType == null)
            {
                return false;
            }

            object? factory;
            try
            {
                factory = Activator.CreateInstance(factoryType);
            }
            catch
            {
                return false;
            }

            if (factory == null)
            {
                return false;
            }

            var createMethod = GetCreateCoordinateSystemMethod(factoryType);
            if (createMethod == null)
            {
                return false;
            }

            var source = createMethod.Invoke(factory, new object[] { sourceCode });
            var dest = createMethod.Invoke(factory, new object[] { destCode });
            if (source == null || dest == null)
            {
                return false;
            }

            var createTransformMethod = GetCreateTransformMethod(factoryType);
            if (createTransformMethod == null)
            {
                return false;
            }

            var transform = createTransformMethod.Invoke(factory, new[] { source, dest });
            if (transform == null)
            {
                return false;
            }

            var transformMethod = GetTransformMethod(transform.GetType(), out var signature);
            if (transformMethod == null)
            {
                return false;
            }

            transformer = new Map3dCoordinateTransformer(transform, transformMethod, signature);
            return true;
        }

        private static bool TryCreateModern(string sourceCode, string destCode, out Map3dCoordinateTransformer? transformer)
        {
            transformer = null;
            var factoryType = FindModernFactoryType();
            if (factoryType == null)
            {
                return false;
            }

            object? factory;
            try
            {
                factory = Activator.CreateInstance(factoryType);
            }
            catch
            {
                return false;
            }

            if (factory == null)
            {
                return false;
            }

            try
            {
                var coordinateSystemsMethod = factoryType.GetMethod("CoordinateSystems", BindingFlags.Public | BindingFlags.Instance);
                var transformationsMethod = factoryType.GetMethod("Transformations", BindingFlags.Public | BindingFlags.Instance);
                if (coordinateSystemsMethod == null || transformationsMethod == null)
                {
                    return false;
                }

                var coordinateSystems = coordinateSystemsMethod.Invoke(factory, null);
                var transformations = transformationsMethod.Invoke(factory, null);
                if (coordinateSystems == null || transformations == null)
                {
                    return false;
                }

                var createCoordinateSystemMethod = coordinateSystems.GetType().GetMethod("CreateFromCode", new[] { typeof(string) });
                if (createCoordinateSystemMethod == null)
                {
                    return false;
                }

                var source = createCoordinateSystemMethod.Invoke(coordinateSystems, new object[] { sourceCode });
                var dest = createCoordinateSystemMethod.Invoke(coordinateSystems, new object[] { destCode });
                if (source == null || dest == null)
                {
                    return false;
                }

                var createTransformationMethod = transformations.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        var parameters = method.GetParameters();
                        return method.Name == "Create" && parameters.Length >= 2;
                    });
                if (createTransformationMethod == null)
                {
                    return false;
                }

                var transform = createTransformationMethod.GetParameters().Length == 3
                    ? createTransformationMethod.Invoke(transformations, new[] { source, dest, false })
                    : createTransformationMethod.Invoke(transformations, new[] { source, dest });
                if (transform == null)
                {
                    return false;
                }

                var coordinateType = transform.GetType().Assembly
                    .GetType("Autodesk.Map.IM.CoordinateSystem.API.Coordinate", throwOnError: false)
                    ?? factoryType.Assembly.GetType("Autodesk.Map.IM.CoordinateSystem.API.Coordinate", throwOnError: false);
                var transformMethod = transform.GetType().GetMethod("Transform", new[] { coordinateType! });
                if (coordinateType == null || transformMethod == null)
                {
                    return false;
                }

                var coordinateCtor = coordinateType.GetConstructor(new[] { typeof(double), typeof(double) });
                var eastingProperty = coordinateType.GetProperty("Easting");
                var northingProperty = coordinateType.GetProperty("Northing");
                if (coordinateCtor == null || eastingProperty == null || northingProperty == null)
                {
                    return false;
                }

                transformer = new Map3dCoordinateTransformer(point =>
                {
                    try
                    {
                        var coordinate = coordinateCtor.Invoke(new object[] { point.X, point.Y });
                        var transformed = transformMethod.Invoke(transform, new[] { coordinate });
                        if (transformed == null)
                        {
                            return ProjectionResult.Failure;
                        }

                        var longitude = Convert.ToDouble(eastingProperty.GetValue(transformed), CultureInfo.InvariantCulture);
                        var latitude = Convert.ToDouble(northingProperty.GetValue(transformed), CultureInfo.InvariantCulture);
                        return ProjectionResult.FromValues(latitude, longitude);
                    }
                    catch
                    {
                        return ProjectionResult.Failure;
                    }
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateMapGuide(string sourceCode, string destCode, out Map3dCoordinateTransformer? transformer)
        {
            transformer = null;
            var factoryType = FindMapGuideFactoryType();
            if (factoryType == null)
            {
                return false;
            }

            object? factory;
            try
            {
                factory = Activator.CreateInstance(factoryType);
            }
            catch
            {
                return false;
            }

            if (factory == null)
            {
                return false;
            }

            try
            {
                var createMethod = factoryType.GetMethod("Create", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (createMethod == null)
                {
                    return false;
                }

                var source = createMethod.Invoke(factory, new object[] { sourceCode });
                var dest = createMethod.Invoke(factory, new object[] { destCode });
                if (source == null || dest == null)
                {
                    return false;
                }

                var getTransformMethod = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "GetTransform", StringComparison.Ordinal))
                        {
                            return false;
                        }

                        return method.GetParameters().Length == 2;
                    });
                if (getTransformMethod == null)
                {
                    return false;
                }

                var transform = getTransformMethod.Invoke(factory, new[] { source, dest });
                if (transform == null)
                {
                    return false;
                }

                var transformMethod = GetTransformMethod(transform.GetType(), out var signature);
                if (transformMethod == null)
                {
                    return false;
                }

                transformer = new Map3dCoordinateTransformer(transform, transformMethod, signature);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Type? FindLegacyFactoryType()
        {
            EnsureMapAssembliesPrepared();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var factoryType = TryGetLegacyFactoryType(assembly);
                if (factoryType != null)
                {
                    return factoryType;
                }
            }

            foreach (var assembly in LoadCandidateMapAssemblies())
            {
                var factoryType = TryGetLegacyFactoryType(assembly);
                if (factoryType != null)
                {
                    return factoryType;
                }
            }

            return null;
        }

        private static Type? FindModernFactoryType()
        {
            EnsureMapAssembliesPrepared();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var factoryType = TryGetModernFactoryType(assembly);
                if (factoryType != null)
                {
                    return factoryType;
                }
            }

            foreach (var assembly in LoadCandidateMapAssemblies())
            {
                var factoryType = TryGetModernFactoryType(assembly);
                if (factoryType != null)
                {
                    return factoryType;
                }
            }

            return null;
        }

        private static Type? FindMapGuideFactoryType()
        {
            EnsureMapAssembliesPrepared();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var factoryType = TryGetMapGuideFactoryType(assembly);
                if (factoryType != null)
                {
                    return factoryType;
                }
            }

            foreach (var assembly in LoadCandidateMapAssemblies())
            {
                var factoryType = TryGetMapGuideFactoryType(assembly);
                if (factoryType != null)
                {
                    return factoryType;
                }
            }

            return null;
        }

        private static IEnumerable<Assembly> LoadCandidateMapAssemblies()
        {
            var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                loaded[assembly.FullName ?? assembly.GetName().Name ?? Guid.NewGuid().ToString("N")] = assembly;
            }

            foreach (var assemblyName in GetCandidateAssemblyNames())
            {
                Assembly? assembly = null;
                try
                {
                    assembly = Assembly.Load(assemblyName);
                }
                catch
                {
                }

                if (assembly == null)
                {
                    continue;
                }

                var key = assembly.FullName ?? assemblyName;
                if (!loaded.ContainsKey(key))
                {
                    loaded[key] = assembly;
                    yield return assembly;
                }
            }

            foreach (var assemblyPath in GetCandidateAssemblyPaths())
            {
                Assembly? assembly = null;
                try
                {
                    assembly = Assembly.LoadFrom(assemblyPath);
                }
                catch
                {
                }

                if (assembly == null)
                {
                    continue;
                }

                var key = assembly.FullName ?? assemblyPath;
                if (!loaded.ContainsKey(key))
                {
                    loaded[key] = assembly;
                    yield return assembly;
                }
            }
        }

        private sealed class MapAssemblyResolverScope : IDisposable
        {
            private readonly ResolveEventHandler _handler;
            private readonly string[] _searchDirectories;

            public MapAssemblyResolverScope(string[] searchDirectories)
            {
                _searchDirectories = searchDirectories ?? Array.Empty<string>();
                _handler = ResolveAssembly;
                AppDomain.CurrentDomain.AssemblyResolve += _handler;
            }

            public void Dispose()
            {
                AppDomain.CurrentDomain.AssemblyResolve -= _handler;
            }

            private Assembly? ResolveAssembly(object? sender, ResolveEventArgs args)
            {
                var requestedName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrWhiteSpace(requestedName))
                {
                    return null;
                }

                _resolvingAssemblyNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!_resolvingAssemblyNames.Add(requestedName))
                {
                    return null;
                }

                try
                {
                    var fileName = requestedName + ".dll";
                    foreach (var directory in _searchDirectories)
                    {
                        var candidate = Path.Combine(directory, fileName);
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        try
                        {
                            return Assembly.LoadFrom(candidate);
                        }
                        catch
                        {
                        }
                    }

                    return null;
                }
                finally
                {
                    _resolvingAssemblyNames.Remove(requestedName);
                }
            }
        }

        private static void EnsureMapAssembliesPrepared()
        {
            lock (MapAssemblySync)
            {
                if (_mapAssemblyResolverInstalled)
                {
                    return;
                }

                _mapAssemblySearchDirectories = GetCandidateBaseDirectories()
                    .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    .Distinct(PathComparer)
                    .ToArray();

                AppDomain.CurrentDomain.AssemblyResolve += ResolveMapAssembly;
                _mapAssemblyResolverInstalled = true;

                foreach (var assemblyPath in GetPreferredAssemblyPaths())
                {
                    try
                    {
                        Assembly.LoadFrom(assemblyPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static Assembly? ResolveMapAssembly(object? sender, ResolveEventArgs args)
        {
            var requestedName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return null;
            }

            _resolvingAssemblyNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!_resolvingAssemblyNames.Add(requestedName))
            {
                return null;
            }

            try
            {
                var fileName = requestedName + ".dll";
                foreach (var directory in GetResolverSearchDirectories(args))
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    try
                    {
                        return Assembly.LoadFrom(candidate);
                    }
                    catch
                    {
                    }
                }

                return null;
            }
            finally
            {
                _resolvingAssemblyNames.Remove(requestedName);
            }
        }

        private static IEnumerable<string> GetResolverSearchDirectories(ResolveEventArgs args)
        {
            var seen = new HashSet<string>(PathComparer);

            foreach (var directory in GetAssemblyRelatedSearchDirectories(args.RequestingAssembly))
            {
                if (seen.Add(directory))
                {
                    yield return directory;
                }
            }

            foreach (var directory in _mapAssemblySearchDirectories)
            {
                if (seen.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        private static IEnumerable<string> GetAssemblyRelatedSearchDirectories(Assembly? assembly)
        {
            if (assembly == null)
            {
                yield break;
            }

            string? assemblyDirectory = null;
            try
            {
                assemblyDirectory = Path.GetDirectoryName(assembly.Location);
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(assemblyDirectory) || !Directory.Exists(assemblyDirectory))
            {
                yield break;
            }

            yield return assemblyDirectory;

            var trimmed = assemblyDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            yield return Path.Combine(trimmed, "Map");
            yield return Path.Combine(trimmed, "bin");
            yield return Path.Combine(trimmed, "Map", "bin");
            yield return Path.Combine(trimmed, "Map", "bin", "GisPlatform");
            yield return Path.Combine(trimmed, "Map", "bin", "FDO");

            string? parent = null;
            try
            {
                parent = Directory.GetParent(trimmed)?.FullName;
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(parent))
            {
                yield break;
            }

            yield return parent;
            yield return Path.Combine(parent, "Map");
            yield return Path.Combine(parent, "bin");
            yield return Path.Combine(parent, "Map", "bin");
            yield return Path.Combine(parent, "Map", "bin", "GisPlatform");
            yield return Path.Combine(parent, "Map", "bin", "FDO");
        }

        private static IEnumerable<string> GetCandidateAssemblyNames()
        {
            yield return "AcMapMgd";
            yield return "AcMapSpatialReference";
            yield return "AcMapSpatialReferenceMgd";
            yield return "AcMapCoordsysCoreMgd";
            yield return "ManagedMapApi";
            yield return "Autodesk.Map.Platform";
            yield return "Autodesk.Gis.Map.ManagedADP";
            yield return "Autodesk.Map.IM.CoordinateSystem.API";
            yield return "Autodesk.Map.IM.CoordinateSystem.Factory";
            yield return "Autodesk.Map.IM.CoordinateSystem.SpatialReferenceApi";
            yield return "OSGeo.MapGuide.Foundation";
            yield return "OSGeo.MapGuide.PlatformBase";
            yield return "OSGeo.MapGuide.Geometry";
        }

        private static IEnumerable<string> GetCandidateAssemblyPaths()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var basePath in GetCandidateBaseDirectories())
            {
                foreach (var fileName in GetCandidateAssemblyFileNames())
                {
                    var path = Path.Combine(basePath, fileName);
                    if (seen.Add(path) && File.Exists(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        private static IEnumerable<string> GetCandidateBaseDirectories()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                yield return baseDirectory;

                var trimmed = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                yield return Path.Combine(trimmed, "Map");
                yield return Path.Combine(trimmed, "bin");
                yield return Path.Combine(trimmed, "Map", "bin");
                yield return Path.Combine(trimmed, "Map", "bin", "GisPlatform");
                yield return Path.Combine(trimmed, "Map", "bin", "FDO");
            }
        }

        private static IEnumerable<string> GetCandidateAssemblyFileNames()
        {
            yield return "AcMapMgd.dll";
            yield return "AcMapSpatialReference.dll";
            yield return "AcMapSpatialReferenceMgd.dll";
            yield return "AcMapCoordsysCoreMgd.dll";
            yield return "ManagedMapApi.dll";
            yield return "Autodesk.Map.Platform.dll";
            yield return "Autodesk.Gis.Map.ManagedADP.dll";
            yield return "Autodesk.Map.IM.CoordinateSystem.API.dll";
            yield return "Autodesk.Map.IM.CoordinateSystem.Factory.dll";
            yield return "Autodesk.Map.IM.CoordinateSystem.SpatialReferenceApi.dll";
            yield return "OSGeo.MapGuide.Foundation.dll";
            yield return "OSGeo.MapGuide.PlatformBase.dll";
            yield return "OSGeo.MapGuide.Geometry.dll";
        }

        private static IEnumerable<string> GetPreferredAssemblyPaths()
        {
            var seen = new HashSet<string>(PathComparer);
            foreach (var basePath in GetCandidateBaseDirectories())
            {
                foreach (var fileName in GetPreferredAssemblyFileNames())
                {
                    var path = Path.Combine(basePath, fileName);
                    if (seen.Add(path) && File.Exists(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        private static IEnumerable<string> GetPreferredAssemblyFileNames()
        {
            yield return "Autodesk.Map.IM.CoordinateSystem.API.dll";
            yield return "Autodesk.Map.IM.CoordinateSystem.SpatialReferenceApi.dll";
            yield return "Autodesk.Map.IM.CoordinateSystem.Factory.dll";
            yield return "AcMapSpatialReference.dll";
            yield return "AcMapSpatialReferenceMgd.dll";
            yield return "ManagedMapApi.dll";
            yield return "OSGeo.MapGuide.Foundation.dll";
            yield return "OSGeo.MapGuide.PlatformBase.dll";
            yield return "OSGeo.MapGuide.Geometry.dll";
        }

        private static Type? TryGetLegacyFactoryType(Assembly? assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            try
            {
                return assembly.GetType("Autodesk.Gis.Map.Platform.CoordinateSystemFactory", throwOnError: false)
                       ?? assembly.GetType("Autodesk.Gis.Map.Platform.CsCoordinateSystemFactory", throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        private static Type? TryGetModernFactoryType(Assembly? assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            try
            {
                return assembly.GetType("Autodesk.Map.IM.CoordinateSystem.Factory.CoordinateSystemFactory", throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        private static Type? TryGetMapGuideFactoryType(Assembly? assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            try
            {
                return assembly.GetType("OSGeo.MapGuide.MgCoordinateSystemFactory", throwOnError: false);
            }
            catch
            {
                return null;
            }
        }

        private readonly record struct ProjectionResult(bool Success, double Latitude, double Longitude)
        {
            public static ProjectionResult Failure => new(false, 0.0, 0.0);

            public static ProjectionResult FromValues(double latitude, double longitude) => new(true, latitude, longitude);
        }

        private static MethodInfo? GetCreateCoordinateSystemMethod(Type factoryType)
        {
            return factoryType.GetMethods()
                .Where(method => method.GetParameters().Length == 1
                                 && method.GetParameters()[0].ParameterType == typeof(string)
                                 && method.Name.IndexOf("Create", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(method => method.Name.Equals("Create", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
        }

        private static MethodInfo? GetCreateTransformMethod(Type factoryType)
        {
            return factoryType.GetMethods()
                .Where(method => method.GetParameters().Length == 2
                                 && method.Name.IndexOf("Transform", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(method => method.Name.StartsWith("Create", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
        }

        private static MethodInfo? GetTransformMethod(Type transformType, out TransformSignature signature)
        {
            signature = TransformSignature.Unknown;
            foreach (var method in transformType.GetMethods())
            {
                if (!string.Equals(method.Name, "Transform", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 2)
                {
                    var first = parameters[0];
                    var second = parameters[1];
                    if (first.ParameterType == typeof(double) && second.ParameterType == typeof(double))
                    {
                        signature = TransformSignature.TwoDoubles;
                        return method;
                    }

                    if (first.ParameterType.IsByRef && second.ParameterType.IsByRef
                        && first.ParameterType.GetElementType() == typeof(double)
                        && second.ParameterType.GetElementType() == typeof(double))
                    {
                        signature = TransformSignature.RefDoubles;
                        return method;
                    }
                }

                if (parameters.Length == 1)
                {
                    if (parameters[0].ParameterType == typeof(double[]))
                    {
                        signature = TransformSignature.DoubleArray;
                        return method;
                    }

                    signature = TransformSignature.SingleObject;
                    return method;
                }
            }

            return null;
        }

        private static object? CreatePointInstance(Type parameterType, double x, double y)
        {
            var ctor = parameterType.GetConstructor(new[] { typeof(double), typeof(double) });
            if (ctor != null)
            {
                return ctor.Invoke(new object[] { x, y });
            }

            var ctor3d = parameterType.GetConstructor(new[] { typeof(double), typeof(double), typeof(double) });
            if (ctor3d != null)
            {
                return ctor3d.Invoke(new object[] { x, y, 0.0 });
            }

            return null;
        }

        private static bool TryReadResult(object? result, out double lat, out double lon)
        {
            lat = 0.0;
            lon = 0.0;
            if (result == null)
            {
                return false;
            }

            if (result is double[] array && array.Length >= 2)
            {
                lon = array[0];
                lat = array[1];
                return true;
            }

            var type = result.GetType();
            var xProperty = type.GetProperty("X") ?? type.GetProperty("Longitude");
            var yProperty = type.GetProperty("Y") ?? type.GetProperty("Latitude");
            if (xProperty != null && yProperty != null)
            {
                lon = Convert.ToDouble(xProperty.GetValue(result), CultureInfo.InvariantCulture);
                lat = Convert.ToDouble(yProperty.GetValue(result), CultureInfo.InvariantCulture);
                return true;
            }

            var xMethod = type.GetMethod("GetX", Type.EmptyTypes) ?? type.GetMethod("X", Type.EmptyTypes);
            var yMethod = type.GetMethod("GetY", Type.EmptyTypes) ?? type.GetMethod("Y", Type.EmptyTypes);
            if (xMethod != null && yMethod != null)
            {
                lon = Convert.ToDouble(xMethod.Invoke(result, null), CultureInfo.InvariantCulture);
                lat = Convert.ToDouble(yMethod.Invoke(result, null), CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        private enum TransformSignature
        {
            Unknown,
            TwoDoubles,
            RefDoubles,
            DoubleArray,
            SingleObject
        }
    }

    internal sealed class UtmCoordinateConverter
    {
        private const double ScaleFactor = 0.9996;
        private const double FalseEasting = 500000.0;
        private const double DegreesPerRadian = 180.0 / Math.PI;
        private const double Nad83MajorAxis = 6378137.0;
        private const double Nad83Flattening = 1.0 / 298.257222101;
        private const double Nad27MajorAxis = 6378206.4;
        private const double Nad27Flattening = 1.0 / 294.9786982;
        private readonly int _zone;
        private readonly double _majorAxis;
        private readonly double _e2;
        private readonly double _ePrime2;
        private readonly double _e1;

        private UtmCoordinateConverter(int zone, double majorAxis, double flattening)
        {
            _zone = zone;
            _majorAxis = majorAxis;
            _e2 = flattening * (2.0 - flattening);
            _ePrime2 = _e2 / (1.0 - _e2);
            var sqrtOneMinusE2 = Math.Sqrt(1.0 - _e2);
            _e1 = (1.0 - sqrtOneMinusE2) / (1.0 + sqrtOneMinusE2);
        }

        public static bool TryCreate(string zoneText, out UtmCoordinateConverter? converter)
        {
            return TryCreate(zoneText, Nad83MajorAxis, Nad83Flattening, out converter);
        }

        public static bool TryCreateNad27(string zoneText, out UtmCoordinateConverter? converter)
        {
            return TryCreate(zoneText, Nad27MajorAxis, Nad27Flattening, out converter);
        }

        private static bool TryCreate(string zoneText, double majorAxis, double flattening, out UtmCoordinateConverter? converter)
        {
            converter = null;
            if (!int.TryParse(zoneText, out var zone))
            {
                return false;
            }

            if (zone < 1 || zone > 60)
            {
                return false;
            }

            converter = new UtmCoordinateConverter(zone, majorAxis, flattening);
            return true;
        }

        public bool TryProject(Point3d point, out double lat, out double lon)
        {
            return TryProject(point.X, point.Y, out lat, out lon);
        }

        public bool TryProjectLatLon(double lat, double lon, out double easting, out double northing)
        {
            easting = 0.0;
            northing = 0.0;

            var latRad = lat / DegreesPerRadian;
            var lonRad = lon / DegreesPerRadian;
            var sinLat = Math.Sin(latRad);
            var cosLat = Math.Cos(latRad);
            var tanLat = Math.Tan(latRad);

            var lonOrigin = (_zone - 1) * 6.0 - 180.0 + 3.0;
            var lonOriginRad = lonOrigin / DegreesPerRadian;

            var n = _majorAxis / Math.Sqrt(1.0 - _e2 * sinLat * sinLat);
            var t = tanLat * tanLat;
            var c = _ePrime2 * cosLat * cosLat;
            var a = cosLat * (lonRad - lonOriginRad);

            var m = _majorAxis * ((1.0 - _e2 / 4.0 - 3.0 * Math.Pow(_e2, 2) / 64.0 - 5.0 * Math.Pow(_e2, 3) / 256.0) * latRad
                                 - (3.0 * _e2 / 8.0 + 3.0 * Math.Pow(_e2, 2) / 32.0 + 45.0 * Math.Pow(_e2, 3) / 1024.0) * Math.Sin(2.0 * latRad)
                                 + (15.0 * Math.Pow(_e2, 2) / 256.0 + 45.0 * Math.Pow(_e2, 3) / 1024.0) * Math.Sin(4.0 * latRad)
                                 - (35.0 * Math.Pow(_e2, 3) / 3072.0) * Math.Sin(6.0 * latRad));

            easting = FalseEasting + ScaleFactor * n
                * (a + (1.0 - t + c) * Math.Pow(a, 3) / 6.0
                   + (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * _ePrime2) * Math.Pow(a, 5) / 120.0);

            northing = ScaleFactor * (m + n * tanLat
                * (a * a / 2.0
                   + (5.0 - t + 9.0 * c + 4.0 * c * c) * Math.Pow(a, 4) / 24.0
                   + (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * _ePrime2) * Math.Pow(a, 6) / 720.0));

            if (lat < 0.0)
            {
                northing += 10000000.0;
            }

            return !(double.IsNaN(easting) || double.IsNaN(northing) || double.IsInfinity(easting) || double.IsInfinity(northing));
        }

        private bool TryProject(double easting, double northing, out double lat, out double lon)
        {
            lat = 0.0;
            lon = 0.0;

            var x = easting - FalseEasting;
            var m = northing / ScaleFactor;
            var mu = m / (_majorAxis * (1.0 - _e2 / 4.0 - 3.0 * Math.Pow(_e2, 2) / 64.0 - 5.0 * Math.Pow(_e2, 3) / 256.0));

            var phi1 = mu
                       + (3.0 * _e1 / 2.0 - 27.0 * Math.Pow(_e1, 3) / 32.0) * Math.Sin(2.0 * mu)
                       + (21.0 * Math.Pow(_e1, 2) / 16.0 - 55.0 * Math.Pow(_e1, 4) / 32.0) * Math.Sin(4.0 * mu)
                       + (151.0 * Math.Pow(_e1, 3) / 96.0) * Math.Sin(6.0 * mu)
                       + (1097.0 * Math.Pow(_e1, 4) / 512.0) * Math.Sin(8.0 * mu);

            var sinPhi1 = Math.Sin(phi1);
            var cosPhi1 = Math.Cos(phi1);
            var tanPhi1 = Math.Tan(phi1);

            var c1 = _ePrime2 * cosPhi1 * cosPhi1;
            var t1 = tanPhi1 * tanPhi1;
            var n1 = _majorAxis / Math.Sqrt(1.0 - _e2 * sinPhi1 * sinPhi1);
            var r1 = _majorAxis * (1.0 - _e2) / Math.Pow(1.0 - _e2 * sinPhi1 * sinPhi1, 1.5);
            var d = x / (n1 * ScaleFactor);

            var latRad = phi1 - (n1 * tanPhi1 / r1)
                * (d * d / 2.0
                   - (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * _ePrime2) * Math.Pow(d, 4) / 24.0
                   + (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * _ePrime2 - 3.0 * c1 * c1) * Math.Pow(d, 6) / 720.0);

            var lonRad = (d
                          - (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3) / 6.0
                          + (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * _ePrime2 + 24.0 * t1 * t1) * Math.Pow(d, 5) / 120.0)
                         / cosPhi1;

            var lonOrigin = (_zone - 1) * 6.0 - 180.0 + 3.0;
            lat = latRad * DegreesPerRadian;
            lon = lonOrigin + lonRad * DegreesPerRadian;

            return !(double.IsNaN(lat) || double.IsNaN(lon) || double.IsInfinity(lat) || double.IsInfinity(lon));
        }
    }
}
