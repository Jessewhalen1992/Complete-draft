using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        internal static bool TryBuildQuarterMapForBoundaryImport(
            Polyline section,
            out Dictionary<QuarterSelection, Polyline> quarterMap)
        {
            quarterMap = new Dictionary<QuarterSelection, Polyline>();
            if (section == null)
            {
                return false;
            }

            return TryBuildQuarterMap(section, out quarterMap, out _);
        }

        internal static string QuarterTokenForBoundaryImport(QuarterSelection quarter)
        {
            return QuarterSelectionToToken(quarter);
        }
    }
}
