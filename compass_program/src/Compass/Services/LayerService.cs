using Autodesk.AutoCAD.DatabaseServices;
using Compass.Infrastructure.Acad;

namespace Compass.Services;

public class LayerService
{
    public void EnsureLayer(Database database, string layerName)
    {
        AcadContext.Run(database, write: true, transaction =>
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(layerName))
            {
                layerTable.UpgradeOpen();
                var record = new LayerTableRecord { Name = layerName };
                layerTable.Add(record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
        });
    }
}
