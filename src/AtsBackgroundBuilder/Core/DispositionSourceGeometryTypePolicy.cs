using System;

namespace AtsBackgroundBuilder.Core
{
    internal static class DispositionSourceGeometryTypePolicy
    {
        public static bool IsMapPolygon(string? dxfName, string? className, string? runtimeTypeName)
        {
            return IsMapPolygonToken(dxfName) ||
                   IsMapPolygonToken(className) ||
                   IsMapPolygonToken(runtimeTypeName);
        }

        private static bool IsMapPolygonToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var token = value.Trim();
            return string.Equals(token, "MPOLYGON", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "POLYGON", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "AcDbMPolygon", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "AcDbPolygon", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "MPolygon", StringComparison.OrdinalIgnoreCase) ||
                   token.IndexOf("MPOLYGON", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
