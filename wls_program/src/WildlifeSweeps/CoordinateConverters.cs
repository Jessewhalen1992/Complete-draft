using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class Map3dCoordinateTransformer
    {
        private readonly object _transform;
        private readonly MethodInfo _transformMethod;
        private readonly TransformSignature _signature;

        private Map3dCoordinateTransformer(object transform, MethodInfo transformMethod, TransformSignature signature)
        {
            _transform = transform;
            _transformMethod = transformMethod;
            _signature = signature;
        }

        public static bool TryCreate(string sourceCode, string destCode, out Map3dCoordinateTransformer? transformer)
        {
            transformer = null;
            var assembly = TryLoadMapAssembly();
            if (assembly == null)
            {
                return false;
            }

            var factoryType = assembly.GetType("Autodesk.Gis.Map.Platform.CoordinateSystemFactory")
                              ?? assembly.GetType("Autodesk.Gis.Map.Platform.CsCoordinateSystemFactory");
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

        public bool TryProject(Point3d point, out double lat, out double lon)
        {
            lat = 0.0;
            lon = 0.0;

            try
            {
                switch (_signature)
                {
                    case TransformSignature.TwoDoubles:
                    {
                        var result = _transformMethod.Invoke(_transform, new object[] { point.X, point.Y });
                        return TryReadResult(result, out lat, out lon);
                    }
                    case TransformSignature.RefDoubles:
                    {
                        var args = new object[] { point.X, point.Y };
                        _transformMethod.Invoke(_transform, args);
                        lon = Convert.ToDouble(args[0], CultureInfo.InvariantCulture);
                        lat = Convert.ToDouble(args[1], CultureInfo.InvariantCulture);
                        return true;
                    }
                    case TransformSignature.DoubleArray:
                    {
                        var args = new[] { point.X, point.Y };
                        var result = _transformMethod.Invoke(_transform, new object[] { args });
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
                        var parameterType = _transformMethod.GetParameters()[0].ParameterType;
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

        private static Assembly? TryLoadMapAssembly()
        {
            try
            {
                return Assembly.Load("AcMapMgd");
            }
            catch
            {
                return null;
            }
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
        private const double MajorAxis = 6378137.0;
        private const double Flattening = 1.0 / 298.257222101;
        private const double ScaleFactor = 0.9996;
        private const double FalseEasting = 500000.0;
        private const double DegreesPerRadian = 180.0 / Math.PI;
        private readonly int _zone;
        private readonly double _e2;
        private readonly double _ePrime2;
        private readonly double _e1;

        private UtmCoordinateConverter(int zone)
        {
            _zone = zone;
            _e2 = Flattening * (2.0 - Flattening);
            _ePrime2 = _e2 / (1.0 - _e2);
            var sqrtOneMinusE2 = Math.Sqrt(1.0 - _e2);
            _e1 = (1.0 - sqrtOneMinusE2) / (1.0 + sqrtOneMinusE2);
        }

        public static bool TryCreate(string zoneText, out UtmCoordinateConverter? converter)
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

            converter = new UtmCoordinateConverter(zone);
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

            var n = MajorAxis / Math.Sqrt(1.0 - _e2 * sinLat * sinLat);
            var t = tanLat * tanLat;
            var c = _ePrime2 * cosLat * cosLat;
            var a = cosLat * (lonRad - lonOriginRad);

            var m = MajorAxis * ((1.0 - _e2 / 4.0 - 3.0 * Math.Pow(_e2, 2) / 64.0 - 5.0 * Math.Pow(_e2, 3) / 256.0) * latRad
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
            var mu = m / (MajorAxis * (1.0 - _e2 / 4.0 - 3.0 * Math.Pow(_e2, 2) / 64.0 - 5.0 * Math.Pow(_e2, 3) / 256.0));

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
            var n1 = MajorAxis / Math.Sqrt(1.0 - _e2 * sinPhi1 * sinPhi1);
            var r1 = MajorAxis * (1.0 - _e2) / Math.Pow(1.0 - _e2 * sinPhi1 * sinPhi1, 1.5);
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
