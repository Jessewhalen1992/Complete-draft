using Autodesk.AutoCAD.DatabaseServices;

namespace Compass.Models;

public class BlockAttributeData
{
    public BlockReference? BlockReference { get; set; }
    public string DrillName { get; set; } = string.Empty;
    public double YCoordinate { get; set; }
}
