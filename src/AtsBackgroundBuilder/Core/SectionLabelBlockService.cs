using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder.Core
{
    internal static class SectionLabelBlockService
    {
        private const string BlockName = "L-SECLBL";

        public static ObjectId Insert(
            BlockTableRecord modelSpace,
            BlockTable blockTable,
            Transaction transaction,
            Editor editor,
            Point3d position,
            SectionKey key)
        {
            if (!blockTable.Has(BlockName))
            {
                editor?.WriteMessage($"\nBUILDSEC: Block '{BlockName}' not found; skipped section label.");
                return ObjectId.Null;
            }

            var blockId = blockTable[BlockName];
            var blockRef = new BlockReference(position, blockId)
            {
                ScaleFactors = new Scale3d(1.0)
            };
            var blockRefId = modelSpace.AppendEntity(blockRef);
            transaction.AddNewlyCreatedDBObject(blockRef, true);

            var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (blockDef.HasAttributeDefinitions)
            {
                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition definition))
                    {
                        continue;
                    }

                    if (definition.Constant)
                    {
                        continue;
                    }

                    var reference = new AttributeReference();
                    reference.SetAttributeFromBlock(definition, blockRef.BlockTransform);
                    blockRef.AttributeCollection.AppendAttribute(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                }
            }

            SetBlockAttribute(blockRef, transaction, "SEC", key.Section);
            SetBlockAttribute(blockRef, transaction, "TWP", key.Township);
            SetBlockAttribute(blockRef, transaction, "RGE", key.Range);
            SetBlockAttribute(blockRef, transaction, "MER", key.Meridian);

            return blockRefId;
        }

        public static bool TryGetFootprint(
            BlockTable blockTable,
            Transaction transaction,
            out double width,
            out double height)
        {
            width = 0;
            height = 0;

            if (blockTable == null || transaction == null || !blockTable.Has(BlockName))
            {
                return false;
            }

            try
            {
                var blockId = blockTable[BlockName];
                var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);

                var found = false;
                var minX = 0.0;
                var minY = 0.0;
                var maxX = 0.0;
                var maxY = 0.0;

                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity))
                    {
                        continue;
                    }

                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!found)
                    {
                        minX = extents.MinPoint.X;
                        minY = extents.MinPoint.Y;
                        maxX = extents.MaxPoint.X;
                        maxY = extents.MaxPoint.Y;
                        found = true;
                    }
                    else
                    {
                        minX = Math.Min(minX, extents.MinPoint.X);
                        minY = Math.Min(minY, extents.MinPoint.Y);
                        maxX = Math.Max(maxX, extents.MaxPoint.X);
                        maxY = Math.Max(maxY, extents.MaxPoint.Y);
                    }
                }

                if (!found)
                {
                    return false;
                }

                width = Math.Max(1.0, maxX - minX);
                height = Math.Max(1.0, maxY - minY);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SetBlockAttribute(BlockReference blockRef, Transaction transaction, string tag, string value)
        {
            if (blockRef == null || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            foreach (ObjectId id in blockRef.AttributeCollection)
            {
                if (!(transaction.GetObject(id, OpenMode.ForWrite) is AttributeReference attr))
                {
                    continue;
                }

                if (string.Equals(attr.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    attr.TextString = value ?? string.Empty;
                }
            }
        }
    }
}
