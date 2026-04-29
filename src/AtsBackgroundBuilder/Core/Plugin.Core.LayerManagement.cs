using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

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

            if (PreserveFinalUsecVariantLayers)
            {
                logger?.WriteLine(
                    $"Cleanup: final build relayer skipped because {PreserveFinalUsecVariantLayersEnvVar} or {DisableFinalUsecOutputRelayerEnvVar} is enabled; preserving L-USEC variant layers.");
                return;
            }

            using var transaction = database.TransactionManager.StartTransaction();
            EnsureLayerWithColor(database, transaction, LayerUsecBase, ResolveUsecLayerColorIndex(LayerUsecBase));
            EnsureLayerWithColor(database, transaction, "L-SEC", ResolveUsecLayerColorIndex("L-SEC"));

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var finalSecVariantIds = FindFinalTwentyRoadAllowanceSecVariantIds(transaction, modelSpace);

            var relayeredZero = 0;
            var relayeredTwenty = 0;
            var relayeredThirty = 0;
            var relayeredToSec = 0;

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
                if (finalSecVariantIds.Contains(id))
                {
                    writable.Layer = "L-SEC";
                    relayeredToSec++;
                }
                else
                {
                    writable.Layer = LayerUsecBase;
                }

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
                $"(0={relayeredZero}, 20.12={relayeredTwenty}, 30.18={relayeredThirty}, sec20.12={relayeredToSec}).");
        }

        private static HashSet<ObjectId> FindFinalTwentyRoadAllowanceSecVariantIds(Transaction transaction, BlockTableRecord modelSpace)
        {
            var result = new HashSet<ObjectId>();
            var twentyRows = new List<(ObjectId Id, Point2d A, Point2d B, double Length, Vector2d Unit)>();
            var zeroRows = new List<(ObjectId Id, Point2d A, Point2d B, double Length, Vector2d Unit)>();
            var thirtyRows = new List<(ObjectId Id, Point2d A, Point2d B, double Length, Vector2d Unit)>();

            foreach (ObjectId id in modelSpace)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity entity) ||
                    entity.IsErased ||
                    entity is not Curve)
                {
                    continue;
                }

                var layer = entity.Layer ?? string.Empty;
                var isTwenty =
                    string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
                var isZero = string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase);
                var isThirty =
                    string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
                if (!isTwenty && !isZero && !isThirty)
                {
                    continue;
                }

                if (!TryReadOpenLinearSegment(entity, out var a, out var b))
                {
                    continue;
                }

                var vector = b - a;
                var length = vector.Length;
                if (length <= 100.0 || Math.Abs(vector.X) < Math.Abs(vector.Y))
                {
                    continue;
                }

                var unit = vector / length;
                if (isTwenty)
                {
                    twentyRows.Add((id, a, b, length, unit));
                }
                else if (isZero)
                {
                    zeroRows.Add((id, a, b, length, unit));
                }
                else
                {
                    thirtyRows.Add((id, a, b, length, unit));
                }
            }

            if (twentyRows.Count == 0 || zeroRows.Count == 0)
            {
                return result;
            }

            const double parallelDotMin = 0.985;
            const double gapTol = 1.25;
            const double overlapMin = 80.0;
            const double thirtyCompanionGapTol = 1.75;

            static double OverlapAlongAxis(Point2d axisA, Point2d axisB, Point2d p0, Point2d p1)
            {
                var axis = axisB - axisA;
                var len = axis.Length;
                if (len <= 1e-6)
                {
                    return 0.0;
                }

                var unit = axis / len;
                var t0 = (p0 - axisA).DotProduct(unit);
                var t1 = (p1 - axisA).DotProduct(unit);
                var min = Math.Min(t0, t1);
                var max = Math.Max(t0, t1);
                return Math.Min(len, max) - Math.Max(0.0, min);
            }

            for (var ti = 0; ti < twentyRows.Count; ti++)
            {
                var twenty = twentyRows[ti];
                for (var zi = 0; zi < zeroRows.Count; zi++)
                {
                    var zero = zeroRows[zi];
                    if (Math.Abs(twenty.Unit.DotProduct(zero.Unit)) < parallelDotMin)
                    {
                        continue;
                    }

                    var overlap = Math.Max(
                        OverlapAlongAxis(twenty.A, twenty.B, zero.A, zero.B),
                        OverlapAlongAxis(zero.A, zero.B, twenty.A, twenty.B));
                    if (overlap < overlapMin)
                    {
                        continue;
                    }

                    var gap = DistancePointToInfiniteLine(Midpoint(twenty.A, twenty.B), zero.A, zero.B);
                    if (Math.Abs(gap - RoadAllowanceSecWidthMeters) > gapTol)
                    {
                        continue;
                    }

                    var hasThirtyCompanion = false;
                    for (var ri = 0; ri < thirtyRows.Count; ri++)
                    {
                        var thirty = thirtyRows[ri];
                        if (Math.Abs(twenty.Unit.DotProduct(thirty.Unit)) < parallelDotMin)
                        {
                            continue;
                        }

                        var thirtyOverlap = Math.Max(
                            OverlapAlongAxis(twenty.A, twenty.B, thirty.A, thirty.B),
                            OverlapAlongAxis(thirty.A, thirty.B, twenty.A, twenty.B));
                        if (thirtyOverlap < overlapMin)
                        {
                            continue;
                        }

                        var gapToZero = DistancePointToInfiniteLine(Midpoint(thirty.A, thirty.B), zero.A, zero.B);
                        var gapToTwenty = DistancePointToInfiniteLine(Midpoint(thirty.A, thirty.B), twenty.A, twenty.B);
                        if (Math.Abs(gapToZero - RoadAllowanceUsecWidthMeters) <= thirtyCompanionGapTol ||
                            Math.Abs(gapToTwenty - CorrectionLinePairGapMeters) <= thirtyCompanionGapTol)
                        {
                            hasThirtyCompanion = true;
                            break;
                        }
                    }

                    if (hasThirtyCompanion)
                    {
                        continue;
                    }

                    result.Add(twenty.Id);
                    result.Add(zero.Id);
                }
            }

            return result;
        }
    }
}
