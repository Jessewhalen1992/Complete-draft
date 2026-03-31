using System;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static void EnsureLayer(Database database, Transaction transaction, string layerName)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                return;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName
            };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool IsUsecLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            if (string.Equals(layerName, LayerUsecBase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecThirty, StringComparison.OrdinalIgnoreCase);
        }

        private static short ResolveUsecLayerColorIndex(string layerName)
        {
            if (string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (string.Equals(layerName, LayerUsecThirty, StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }

            return 7;
        }

        private static void EnsureLayerWithColor(Database database, Transaction transaction, string layerName, short colorIndex)
        {
            if (database == null || transaction == null || string.IsNullOrWhiteSpace(layerName))
            {
                return;
            }

            if (colorIndex < 1 || colorIndex > 255)
            {
                colorIndex = 7;
            }

            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                var layerId = table[layerName];
                var layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForWrite);
                var targetColor = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
                if (layer.Color != targetColor)
                {
                    layer.Color = targetColor;
                }

                return;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                IsOff = false
            };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void SetLayerVisibility(
            Database database,
            Transaction transaction,
            string layerName,
            bool isOff,
            bool isPlottable)
        {
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!table.Has(layerName))
            {
                return;
            }

            var layerId = table[layerName];
            var layer = (LayerTableRecord)transaction.GetObject(layerId, OpenMode.ForWrite);
            layer.IsOff = isOff;
            layer.IsPlottable = isPlottable;
        }

        private static bool IsFinalUsecOutputVariantLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            return string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
        }

        private static void NormalizeFinalUsecOutputLayers(Database database, Logger? logger)
        {
            if (database == null)
            {
                return;
            }

            using var transaction = database.TransactionManager.StartTransaction();
            EnsureLayerWithColor(database, transaction, LayerUsecBase, ResolveUsecLayerColorIndex(LayerUsecBase));

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var relayeredZero = 0;
            var relayeredTwenty = 0;
            var relayeredThirty = 0;

            foreach (ObjectId id in modelSpace)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity entity) ||
                    entity.IsErased ||
                    entity is not Curve)
                {
                    continue;
                }

                var sourceLayer = entity.Layer ?? string.Empty;
                if (!IsFinalUsecOutputVariantLayer(sourceLayer))
                {
                    continue;
                }

                var writable = (Entity)transaction.GetObject(id, OpenMode.ForWrite, false);
                writable.Layer = LayerUsecBase;

                if (string.Equals(sourceLayer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                {
                    relayeredZero++;
                }
                else if (string.Equals(sourceLayer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(sourceLayer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase))
                {
                    relayeredTwenty++;
                }
                else
                {
                    relayeredThirty++;
                }
            }

            transaction.Commit();
            logger?.WriteLine(
                $"Cleanup: final build relayer converted {relayeredZero + relayeredTwenty + relayeredThirty} L-USEC variant curve(s) to {LayerUsecBase} " +
                $"(0={relayeredZero}, 20.12={relayeredTwenty}, 30.18={relayeredThirty}).");
        }
    }
}
