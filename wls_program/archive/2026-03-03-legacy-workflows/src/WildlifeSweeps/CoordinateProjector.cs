using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class CoordinateProjector
    {
        private readonly Map3dCoordinateTransformer? _transformer;
        private readonly UtmCoordinateConverter? _utmConverter;

        private CoordinateProjector(Map3dCoordinateTransformer? transformer, UtmCoordinateConverter? utmConverter)
        {
            _transformer = transformer;
            _utmConverter = utmConverter;
        }

        public bool HasProjection => _transformer != null;
        public bool HasFallback => _utmConverter != null;

        public static CoordinateProjector Create(string utmZone)
        {
            var sourceCoordinateSystem = utmZone == "12" ? "UTM83-12" : "UTM83-11";
            Map3dCoordinateTransformer.TryCreate(sourceCoordinateSystem, "LL83", out var transformer);
            UtmCoordinateConverter.TryCreate(utmZone, out var utmConverter);
            return new CoordinateProjector(transformer, utmConverter);
        }

        public bool TryProject(Point3d point, out double lat, out double lon)
        {
            if (_transformer != null && _transformer.TryProject(point, out lat, out lon))
            {
                return true;
            }

            if (_utmConverter != null && _utmConverter.TryProject(point, out lat, out lon))
            {
                return true;
            }

            lat = 0.0;
            lon = 0.0;
            return false;
        }
    }
}
